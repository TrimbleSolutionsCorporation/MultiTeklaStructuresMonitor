namespace MultiTeklaStructuresMonitor.Interop
{
    using System.Diagnostics;
    using System.Net;
    using System.Net.Sockets;

    using Tekla.AppRedirect.Helpers;

    public static class DriverStarter
    {
        private static readonly string CurrentRunningPath = Directory.GetParent(typeof(DriverStarter).Assembly.Location)!.FullName;
        private static readonly string ServiceExe = "TeklaGrpcApiService.exe";
        private static readonly string BasePath = Path.Combine(CurrentRunningPath, "TeklaGrpcApiService");
        private static readonly string BaseExec = Path.Combine(BasePath, ServiceExe);

        public static GrpcServiceClient? StartDriverAndServer(InstallDirData dirData, ILogger logger)
        {
            var deploymentPath = Path.Combine(CurrentRunningPath, dirData.TSVersionDir);
            var driverDeploymentExePath = Path.Combine(deploymentPath, ServiceExe);

            var pidFile = Path.Combine(deploymentPath, "pid.txt");
            var (pidOfRunningProcess, currentPort) = ReadPidFromFile(pidFile);

            if (pidOfRunningProcess > 0 && IsProcessRunning(pidOfRunningProcess))
            {
                return GetClientApp(dirData, pidOfRunningProcess, currentPort);
            }

            // deploy TeklaGrpcApiService.exe into version folder if not exists
            // this is stricly not needed if patching the app config and then starting the exe.  Ive not tested it tought
            if (!DeployDriverToVersionSpecifFolders(dirData, logger, deploymentPath, driverDeploymentExePath))
            {
                return null;
            }

            // finally start the server process
            // get a free port
            var portNumber = GetFreePort();
            var args = $"-p {portNumber} -l \"{Path.Combine(deploymentPath, $"{portNumber}-log.txt")}\"";
            var envs = new Dictionary<string, string>
            {
                // session name console is require so it matches the TS Session when started from Start menu
                // adjust if for whatever reason you have more exotic session names while starting TS (example admin mode - dont run in admin tought)
                { "SESSIONNAME", "Console" }
            };
            var pid = ExecuteCommand(driverDeploymentExePath, args, envs, deploymentPath, logger, false);

            // write the pid to file
            File.WriteAllText(pidFile, $"{pid}:{portNumber}");

            // means the process is already running return client for existent process
            return GetClientApp(dirData, pid, portNumber);
        }

        private static bool DeployDriverToVersionSpecifFolders(InstallDirData dirData, ILogger logger, string deploymentPath, string driverDeploymentExePath)
        {
            // patch the app config for the GRPC server
            var binDir = Path.Combine(dirData.MainDir, dirData.TSVersionDir, "bin");
            var version = Version.Parse(dirData.ProductVersion);

            if (!File.Exists(Path.Combine(binDir, "TeklaStructures.exe.config")))
            {
                return false;
            }

            if (!File.Exists(driverDeploymentExePath))
            {
                try
                {


                    Directory.CreateDirectory(deploymentPath);

                    DropBasePackageToFolder(BasePath, deploymentPath);

                    if (version.Major < 224)
                    {
                        logger.LogInformation($"Before 2024, We will patch app config files without code base elements");
                        TsPatchHelpers.PatchExeFileUsingBinFolderUsingGac(driverDeploymentExePath, binDir);
                    }
                    else
                    {
                        logger.LogInformation($"After 2024, We will patch app config files with code base elements");
                        TsPatchHelpers.PatchExeFileUsingBinFolder(driverDeploymentExePath, binDir);
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    logger.LogCritical($"Wasnt able to register driver: {ex.Message}");
                    return false;
                }
            }
            else
            {
                logger.LogInformation($"Driver already deployed to {driverDeploymentExePath}");

                // redo the app configs always at start
                // this is needed to make sure the app config is always correct
                // and not corrupted by the user
                if (File.Exists($"{driverDeploymentExePath}.config"))
                {
                    File.Delete($"{driverDeploymentExePath}.config");                    
                }

                File.Copy(Path.Combine(BasePath, $"{ServiceExe}.config"), $"{driverDeploymentExePath}.config", true);

                // redo app config
                if (version.Major < 224)
                {
                    logger.LogInformation($"Before 2024, We will patch app config files without code base elements");
                    TsPatchHelpers.PatchExeFileUsingBinFolderUsingGac(driverDeploymentExePath, binDir);
                }
                else
                {
                    logger.LogInformation($"After 2024, We will patch app config files with code base elements");
                    TsPatchHelpers.PatchExeFileUsingBinFolder(driverDeploymentExePath, binDir);
                }
            }

            return true;
        }

        private static GrpcServiceClient? GetClientApp(InstallDirData dirData, int pidOfRunningProcess, int port)
        {
            // means the process is already running return client for existent process
            var client = new GrpcServiceClient(dirData, pidOfRunningProcess, port);
            var data = client.Ping("Server");
            if (data == $"Hello, Server im node: {pidOfRunningProcess}")
            {
                return client;
            }

            return null;
        }

        private static void DropBasePackageToFolder(string basePath, string deploymentPath)
        {
            Directory.CreateDirectory(deploymentPath);

            var files = Directory.GetFiles(basePath);
            foreach (var file in files)
            {
                var destFile = Path.Combine(deploymentPath, Path.GetFileName(file));
                File.Copy(file, destFile, true);
            }            
        }

        private static int GetFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;            
        }

        public static int ExecuteCommand(string name, string args, Dictionary<string, string> env, string workDir, ILogger logger, bool waitForEnd = true)
        {
            var startInfo = new ProcessStartInfo
            {
                WindowStyle = ProcessWindowStyle.Normal,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                FileName = name,
                Arguments = args,
                WorkingDirectory = workDir
            };

            if (env != null)
            {
                foreach (var item in env)
                {
                    startInfo.Environment.Add(item.Key, item.Value);
                }
            }

            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

            process.OutputDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    logger.LogInformation(e.Data);
                    Trace.WriteLine(e.Data);

                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    logger.LogError(e.Data);
                    Trace.WriteLine(e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (waitForEnd)
            {
                process.WaitForExit();
            }

            return process.Id;
        }

        private static Tuple<int,int> ReadPidFromFile(string pidofRunningProcessFile)
        {
            if (File.Exists(pidofRunningProcessFile))
            {
                var data = File.ReadAllText(pidofRunningProcessFile).Trim().Split(":");

                return new Tuple<int, int>(Int32.Parse(data[0]), Int32.Parse(data[01]));
            }

            return new Tuple<int, int>(0, 0);
        }

        private static bool IsProcessRunning(int pid)
        {
            try
            {
                Process.GetProcessById(pid);
                return true;
            }
            catch (Exception)
            {
                return false;
            }

        }
    }
}
