using Autis.PLC.Common.Structs;
using Autis.PLC.Enums;
using Autis.PLC.S7;
using Serilog;
using TestPlan.Logic.Configuration;
using TestPlan.Logic.Interfaces;
using TestPlan.Logic.Models;
using TestPlan.Logic.Services.Monitoring;

namespace TestPlan.Logic.Services.StartCycle
{
    /// <summary>
    /// Clase de escucha de eventos de inicio de ciclo basado en la lectura a PLC
    /// </summary>
    internal class PLCStartCycleListener : IStartCycleTrigger, IDisposable
    {
        #region Properties
        //Servicio 
        private DummyService _dummyService = new DummyService();

        /// <summary>
        /// Token para cancelar esta tarea
        /// </summary>
        private readonly CancellationTokenSource _cts;
        /// <summary>
        /// Tarea para la escucha del evento
        /// </summary>
        private Task? _readingTask;
        /// <summary>
        /// Tiempo en ms para comprobar la lectura del evento
        /// </summary>
        private const int C_PollingInterval = 100;
        /// <summary>
        /// Db del PLC
        /// </summary>
        private readonly PlcDb350X _plc;
        /// <summary>
        /// Id de la estación
        /// </summary>
        private int _stationId;
        /// <summary>
        /// Id del slot
        /// </summary>
        private int _slotId;
        #endregion

        #region Events
        /// <summary>
        /// // Evento para notificar el inicio del ciclo (Parte clave del Observer)
        /// </summary>
        public event EventHandler<CycleDataModel>? LaunchCycle;
        #endregion

        #region Constructor
        /// <summary>
        /// Constructor
        /// </summary>
        public PLCStartCycleListener(int stationId,int slotID)
        {
            _cts = new CancellationTokenSource();

            const int C_FirstDb = 3500;
            int db = C_FirstDb + stationId; // DB3500, DB3501, DB3502, etc.

            // Asigna el id de la estación y el slot
            _stationId = stationId;
            _slotId = slotID;

            // Obtiene los datos de configuración del PLC
            string ip = Config.Instance.General.ConfiguracionPLC.IP;
            short rack = (short)Config.Instance.General.ConfiguracionPLC.Rack;
            short slot = (short)Config.Instance.General.ConfiguracionPLC.Slot;

            // Crea el objeto PLC
            _plc = new PlcDb350X(db, ip, rack, slot);
        }
        #endregion

        #region TestPlan Cycle
        /// <summary>
        /// Iniciar la escucha de los eventos
        /// </summary>
        public void StartListening()
        {
            var _connected = false;

            if (!AppConfig.TestPlan.BypassPLCReadyCheck)
            {
                // Abre la conexión con el PLC
                _connected = true;
            }
            if (_connected || AppConfig.TestPlan.BypassPLCReadyCheck)
            {
                GlobalStationMonitor.Instance.StationMonitor.UpdateSlotStatus(_stationId, _slotId, SlotModel.SlotState.Waiting, "Connected to the PLC");

            _readingTask = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    try
                    {
                        CycleDataModel? startCycleData = null;

                            if (_dummyService.IsDummy())
                            {
                                GlobalStationMonitor.Instance.StationMonitor.UpdateSlotStatus(_stationId, _slotId, SlotModel.SlotState.Waiting, "Launching Dummy Test");
                                startCycleData = _dummyService.GetStartCycleData();
                                OnLaunchCycle(startCycleData);
                            }
                            else if (AppConfig.TestPlan.BypassPLCReadyCheck)
                            {
                                GlobalStationMonitor.Instance.StationMonitor.UpdateSlotStatus(_stationId, _slotId, SlotModel.SlotState.Waiting, "Bypassed PLC Ready (Debug)");
                                startCycleData = new CycleDataModel { SNR = AppConfig.TestPlan.SNR,SlotId=_slotId,StationId=_stationId,DeviceTimeStamp = DateTime.Now };
                                OnLaunchCycle(startCycleData);
                            }
                            else
                            {
                                try
                                {
                                    startCycleData = ReadPlcData();
                                }
                                catch (Exception ex)
                                {
                                    GlobalStationMonitor.Instance.StationMonitor.UpdateSlotStatus(_stationId, _slotId, SlotModel.SlotState.Waiting, "Error reading PLC Trigger");
                                    Log.Error(ex, "Error reading PLC Trigger");
                                }

                                if (startCycleData != null)
                                {
                                    GlobalStationMonitor.Instance.StationMonitor.UpdateSlotStatus(_stationId, _slotId, SlotModel.SlotState.Waiting, "Launching Normal Test");
                                    OnLaunchCycle(startCycleData);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            GlobalStationMonitor.Instance.StationMonitor.UpdateSlotStatus(_stationId, _slotId, SlotModel.SlotState.Waiting, ex.ToString());
                            Log.Error(ex, "");
                        }

                        await Task.Delay(C_PollingInterval, _cts.Token);
                    }
                }, _cts.Token);
            } // Fin del If de conexion 
            else
            {
                // TODO:GO - Lanzar un pop up mostrando el error y una vz responde al pop up se cerrara la aplicacion 
                // Si no se conecta, lanza el evento de error
                GlobalStationMonitor.Instance.StationMonitor.UpdateSlotStatus(_stationId, _slotId, SlotModel.SlotState.Waiting, "Error Connecting to the PLC");
            }
        }

        /// <summary>
        /// Lanzar el evento de inicio de ciclo
        /// </summary>
        private void OnLaunchCycle(CycleDataModel startCycleData)
        {
            Log.Debug($"[SLOT {_slotId}] OnLaunchCycle fired");
            LaunchCycle?.Invoke(this, startCycleData);
        }
        #endregion

        #region PLC Functions
            /// <summary>
            /// Lee el PLC para comprobar si se puede empezar el ciclo (RDY = true)
            /// y si es así, devuelve los datos necesarios para lanzar el ciclo
            /// </summary>
            /// <returns>Devuelve los datos de inicio de ciclo o null en caso de que no esté listo</returns>
        private CycleDataModel? ReadPlcData()
        {
            // Lee la variaable Ready del PLC
            object? readyObject = _plc.ReadValue(eStationStatus350X.Ready);

            // Comprueba si el ciclo está lista para empezar
            if (readyObject != null && (bool)readyObject)
            {
                // Lee todos los datos del PLC
                CommonDb350X data = _plc.Read();

                // Crea y configura el modelo de datos para iniciar el ciclo
                CycleDataModel startCycleData = new CycleDataModel
                {
                    // DEBUG: Temporalmente se fuerza el SlotId a 1 para propósitos de depuración
                    // En producción debería usar: data.process.idSlot
                    SlotId = _slotId,
                    StationId= _stationId,

                    // Asigna la marca de tiempo actual de la estación
                    DeviceTimeStamp = data.Process.TsActual,
                };

                return startCycleData;
            }

            // El ready no está activo
            return null;
        }

        /// <summary>
        /// Escribe en el PLC para simular el evento de inicio de ciclo.
        /// Espera 5 segundos y después escribe true en el PLC (utilizando una tarea asíncrona).
        /// Esta función solo se utiliza para desarrollo y pruebas.
        /// </summary>
        private void SimulateTriggerAsync(int delay, CancellationTokenSource cts)
        {
            Task.Run(async () =>
            {
                // Tiempo de espera antes de generar el trigger simulado
                await Task.Delay(delay);
                Log.Information("Simulate Trigger PLC...");
                // Simula el ID del slot
                for (int i = 0; i < 100; i++)
                {
                    if (_cts.IsCancellationRequested)
                    {
                        return;
                    }
                    // Escribe varias veces el valor debido a que la programación del PLC lo borra automáticamente
                    _plc.WriteValue(eStationStatus350X.Ready, true);
                }
            }, cts.Token);
        }

        #endregion

        #region Stop / Dispose
        /// <summary>
        /// Detiene la escucha
        /// </summary>
        public void StopListening()
        {
            Log.Information("Stop PLC Cycle Listener...");
            _cts.Cancel();

            _readingTask?.Wait(TimeSpan.FromSeconds(1));
        }
        /// <summary>
        /// Dispose
        /// </summary>
        public void Dispose() => StopListening();
        #endregion
    }
}
