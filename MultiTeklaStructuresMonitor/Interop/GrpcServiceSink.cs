namespace MultiTeklaStructuresMonitor.Interop
{
    using Microsoft.Win32;

    using MultiTeklaStructuresMonitor.Helpers;

    public class GrpcServiceSink
    {
        private static readonly string CurrentRunningPath = Directory.GetParent(typeof(DriverStarter).Assembly.Location)!.FullName;
        private static readonly List<GrpcServiceClient> clients = new List<GrpcServiceClient>();
        private readonly ILogger logger;

        private  GrpcServiceSink(ILogger logger)
        {
            this.logger = logger;
        }

        public static GrpcServiceSink CreateInstance(ILogger logger)
        {
            // read all registries for installations path from Computer\HKEY_LOCAL_MACHINE\SOFTWARE\Trimble\Tekla Structures\VERSION\setup key MainDir and Version where VERSION can be anything
            var installDirData = RegistryHelpers.ReadInstalledApplications(logger);

            // start a grpc server for each installation path
            foreach (var installDir in installDirData)
            {
                var client = DriverStarter.StartDriverAndServer(installDir, logger);

                if (client != null)
                {
                    clients.Add(client);
                }
            }

            return new GrpcServiceSink(logger);
        }

        public List<string> GetAllOpenModels()
        {
            var openModels = new List<string>();
            foreach (var client in clients)
            {
                var openModelReply = client.GetOpenModel("GetOpenModel");
                openModels.Add($"Client: {client.InstallData.TSVersionDir} : {client.InstallData.ProductVersion} : Status: {openModelReply}");
            }

            return openModels;
        }

        public List<string> RestartDrivers()
        {
            var replyMessages = new List<string>();
            foreach (var client in clients)
            {

                try
                {
                    var openModelReply = client.StopService();
                    replyMessages.Add($"Client: {client.InstallData.TSVersionDir} : {client.InstallData.ProductVersion} : Status: {openModelReply}");

                }
                catch (Exception)
                {

                }            
            }

            // clear the list of clients
            clients.Clear();

            // register all clients again
            var installDirData = RegistryHelpers.ReadInstalledApplications(this.logger);

            foreach (var installDir in installDirData)
            {
                var client = DriverStarter.StartDriverAndServer(installDir, this.logger);
                if (client != null)
                {
                    clients.Add(client);
                }
            }

            return GetAllOpenModels();
        }
    }
}
