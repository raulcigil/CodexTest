using Serilog;
using TestPlan.Logic.Configuration;
using TestPlan.Logic.Interfaces;
using TestPlan.Logic.Models;

namespace TestPlan.Logic.Services.StartCycle
{
    /// <summary>
    /// Clase de escucha de eventos de inicio de ciclo basado en la lectura a PLC
    /// </summary>
    internal class WaitFiveStartCycleListener : IStartCycleTrigger, IDisposable
    {
        /// <summary>
        /// Identificador de estación
        /// </summary>
        private readonly int _stationId;

        /// <summary>
        /// Identificador de slot
        /// </summary>
        private readonly int _slotId;

        /// <summary>
        /// Habilitada la notificación de alarmas
        /// </summary>
        private bool _isEnabled = false;

        /// <summary>
        /// Token para cancelar esta tarea
        /// </summary>
        private CancellationTokenSource _cts = new CancellationTokenSource();
        /// <summary>
        /// Tarea para la escucha del evento
        /// </summary>
        private Task _readingTask;
        /// <summary>
        /// Tiempo en ms para comprobar la lectura del evento
        /// </summary>
        private const int POLLING_INTERVAL = 200;
        /// <summary>
        /// // Evento para notificar el inicio del ciclo (Parte clave del Observer)
        /// </summary>
        public event EventHandler<CycleDataModel>? LaunchCycle;

        /// <summary>
        /// Constructor
        /// </summary>
        public WaitFiveStartCycleListener(int stationId, int slotId)
        {
            _stationId = stationId;
            _slotId = slotId;
            _readingTask = Task.CompletedTask;
        }
        /// <summary>
        /// Iniciar la escucha de los eventos
        /// </summary>
        public void StartListening()
        {
            if (!_isEnabled)
            {
                _cts = new CancellationTokenSource();
                _isEnabled = true;
                Log.Information("StartListening WaitFiveStartCycleListener");

                _readingTask = Task.Run(async () =>
                {
                    while (!_cts.IsCancellationRequested && _isEnabled)
                    {
                        try
                        {
                            //Simular la espera del evento
                            Task.Delay(AppConfig.TestPlan.C_SimulationTime).Wait();
                            CycleDataModel startCycleData = new CycleDataModel();
                      
                            startCycleData.SlotId = _slotId;
                            startCycleData.StationId = _stationId;
                    
                            OnLaunchCycle(startCycleData); // Notificar a los observadores
                            //Terminar esta escucha después de lanzar el ciclo                            
                            break;
                        }
                        catch (Exception ex)
                        {
                            StopListening();
                            // Manejo de errores
                            Log.Error(ex, "");
                        }
                        await Task.Delay(POLLING_INTERVAL, _cts.Token);
                    }
                }, _cts.Token);

            }
        }

        /// <summary>
        /// Detiene la escucha
        /// </summary>
        public async void StopListening()
        {
            _isEnabled = false;
            //_readingTask?.WaitAsync(_cts.Token);
            _cts.Cancel();
            Log.Debug("StopListening WaitFiveStartCycleListener");

        }
        /// <summary>
        /// Lanzar el evento de inicio de ciclo
        /// </summary>
        private void OnLaunchCycle(CycleDataModel startCycleData)
        {
            //data.IsManualTestPlan = false;
            LaunchCycle?.Invoke(this, startCycleData);
        }

        /// <summary>
        /// Dispose
        /// </summary>
        public void Dispose() => StopListening();
    }
}
