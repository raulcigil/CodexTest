namespace TestPlan.Logic.Services.Monitoring
{
    public class GlobalMonitor
    {
        /// <summary>
        /// StationMonitor is a singleton that monitors the status of all stations and slots.
        /// </summary>
        public StationMonitor StationMonitor { get; } = new();
        /// <summary>
        /// DBUSMonitor is a singleton that monitors the status of all DBUS connections.
        /// </summary>
        public DBUSMonitor DBUSMonitor { get; } = new();
        /// <summary>
        /// CylcicMonitor is a singleton that monitors the status of all cyclic processes.
        /// </summary>
        public CyclicMonitor CyclicMonitor { get; } = new();  // << Nuevo servicio

        #region Auxiliar Methods
        public void ResetAll()
        {

        }
        #endregion 
    }
}
