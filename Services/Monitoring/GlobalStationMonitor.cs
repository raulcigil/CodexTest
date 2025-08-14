
namespace TestPlan.Logic.Services.Monitoring
{

    public static class GlobalStationMonitor
    {
        private static readonly GlobalMonitor _instance = new GlobalMonitor();
        public static GlobalMonitor Instance => _instance;
    }

}