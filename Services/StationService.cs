using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Org.BouncyCastle.Bcpg.Sig;
using Serilog;
using TestPlan.Entities.Views;
using TestPlan.Logic.Interfaces;
using TestPlan.Logic.Models;
using TestPlan.Logic.OPCUa;
using TestPlan.Logic.Services.Cycle;
using TestPlan.Logic.Services.Monitoring;

namespace TestPlan.Logic.Services
{
    /// <summary>
    /// Service responsible for managing a single test station.
    /// </summary>
    public class StationService : IDisposable
    {
        #region Properties

        /// <summary>
        /// Datos de la estación que se propagan a los slots y acciones.
        /// </summary>
        private StationModel _stationData = new StationModel();

        /// <summary>
        /// Task associated with the async station loop.
        /// </summary>
        private Task? _stationTask;

        /// <summary>
        /// Cancellation token source for stopping the station loop.
        /// </summary>
        private CancellationTokenSource _cts = new CancellationTokenSource();

        /// <summary>
        /// Trigger listener service that listens for test cycle start events.
        /// </summary>
        IStartCycleTrigger? _triggerListener;

        /// <summary>
        /// Currently active cycle service for this station.
        /// </summary>
        CycleService? _cycle;


        /// <summary>
        /// Station configuration loaded from the database.
        /// </summary>
        private ConfigStationView? _stationConfig;

        /// <summary>
        /// List of slot workers for this station.
        /// </summary>
        private readonly List<SlotWorker>? _slotWorkers = new();

        #endregion

        #region Constructor

        /// <summary>
        /// Constructor
        /// </summary>
        public StationService(int stationId)
        {
            _stationData.StationId = stationId;
        }

        #endregion

        #region Startup Loop
        /// <summary>
        /// Starts the background station loop in its own task.
        /// </summary>
        public void StartStation()
        {
            _stationTask = Task.Run(async () => await Start(_cts.Token));
        }

        /// <summary>
        /// Main loop for initializing and maintaining the station.
        /// </summary>
        private async Task Start(CancellationToken token)
        {
            // Actualiza el estado de la estación a "Iniciando"
            GlobalStationMonitor.Instance.StationMonitor.UpdateStationStatus(_stationData.StationId, StationModel.StationState.Starting, "Initializing station...");

            // Bucle principal que se ejecuta mientras no se cancele el token
            while (!token.IsCancellationRequested)
            {
                try
                {
                    //Comparacion de Hash de instancias 
                    //Console.WriteLine("[StationService] MONITOR HASH = " + GlobalStationMonitor.Instance.GetHashCode());

                    // Carga la configuración de la estación a partir del ID
                    using (var configLogic = new ConfigStationsLogic())
                        _stationConfig = configLogic.GetConfigStation(_stationData.StationId);

                    // Carga la configuración de los slots
                    using var slotLogic = new ConfigSlotsLogic();
                    var slots = slotLogic.GetConfigSlots(_stationData.StationId);
                    
                    // Por cada slot, crea y arranca un SlotWorker
                    foreach (var slot in slots)
                    {
                        // Inicializa el modelo de datos del slot
                        _stationData.Slots.Add(new SlotModel(slot));

                        var slotWorker = new SlotWorker(_stationData, slot.ID_Slot);
                        _slotWorkers.Add(slotWorker);
                        slotWorker.Start(); // Cada slot ejecuta su propio test
                    }

                    // Mantiene la estación viva hasta que se cancele
                    await Task.Delay(Timeout.Infinite, token);
                }
                // Captura la excepción cuando se cancela la operación
                catch (OperationCanceledException)
                {
                    GlobalStationMonitor.Instance.StationMonitor.UpdateStationStatus(_stationData.StationId, StationModel.StationState.Stopped, "Station Stopped by cancellation");
                    Log.Information($"Station {_stationData.StationId} stopped by cancellation.");
                    break;
                }
                // Captura cualquier otra excepción y actualiza el estado a error
                catch (Exception ex)
                {
                    Log.Error(ex, $"Error in station {_stationData.StationId}");
                    //ErrA00042
                    GlobalStationMonitor.Instance.StationMonitor.UpdateStationStatus(_stationData.StationId, StationModel.StationState.Error, ex.Message);
                    // Espera 10 segundos antes de reintentar
                    await Task.Delay(TimeSpan.FromSeconds(10), token);
                }
            }
        }
        #endregion

        #region Stop / Dispose

        /// <summary>
        /// Stops the station and the trigger listener.
        /// </summary>
        public void Stop()
        {
            _triggerListener?.StopListening();
            _cts.Cancel();

            try
            {
                _stationTask?.Wait();
            }
            catch (AggregateException ex) when (ex.InnerException is OperationCanceledException)
            {
                // Ignorar cancelaciones esperadas
            }
        }

        /// <summary>
        /// Disposes of resources and stops the station.
        /// </summary>
        public void Dispose()
        {
            Stop();
            _cts.Dispose();
        }

        #endregion

        #region Monitoring & Alarms

        /// <summary>
        /// Sends alarm alert to the current cycle.
        /// </summary>
        internal void AlarmAlert()
        {
            _cycle?.AlarmAlert();
        }

        #endregion

    }
}
