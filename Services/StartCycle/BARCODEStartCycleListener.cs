using Org.BouncyCastle.Bcpg.Sig;
using Serilog;
using TestPlan.Entities.Enumeraciones;
using TestPlan.Entities.Extensions;
using TestPlan.Logic.Interfaces;
using TestPlan.Logic.Models;
using TestPlan.Logic.Prompt;
using TestPlan.Logic.Routing;

namespace TestPlan.Logic.Services.StartCycle
{
    /// <summary>
    /// Barcode Start Cycle Listener.
    /// </summary>
    internal class BARCODEStartCycleListener : IStartCycleTrigger, ISerialReceiver, IDisposable
    {
        private readonly StationModel _stationData;

        private CancellationTokenSource _cts;
        private Task? _readingTask;
        /// <summary>
        /// Esperar a recibir el Serial Number (SNR) del lector.
        /// </summary>
        private TaskCompletionSource<string>? _serialTcs;
        /// <summary>
        /// SlotId del lector.
        /// </summary>
        private readonly string _slotId;
        /// <summary>
        /// Reading mode del lector.
        /// </summary>
        private readonly eSnrReadingMode _readerMode;
        /// <summary>
        /// Evento que se dispara cuando se recibe un Serial Number (SNR) y se debe iniciar un ciclo.
        /// </summary>
        public event EventHandler<CycleDataModel>? LaunchCycle;

        /// <summary>
        /// Información del slot
        /// </summary>
        private readonly SlotModel _slot = null;
        /// <summary>
        /// cconstructor.
        /// </summary>
        /// <param name="stationId"></param>
        /// <param name="slotId"></param>
        /// <param name="_slot"></param>
        public BARCODEStartCycleListener(StationModel stationData,  SlotModel slot)
        {
            _slot = slot;
            _stationData = stationData;
            _slotId = _slot.SlotId.ToString();
            _readerMode = _slot.Slot.ReaderCombined;
        }
        /// <summary>
        /// Empieza a escuchar el Serial Number (SNR) del lector.
        /// </summary>
        public void StartListening()
        {
            _cts = new();

            _readingTask = Task.Run(async () =>
            {
                _serialTcs = new TaskCompletionSource<string>();

                // Verificar si ya hay un SNR recibido antes de registrarse
                if (SerialRouter.TryGetCachedSerial(_slotId, out var earlySnr))
                {
                    Log.Information($"[{_slotId}] SNR ya disponible antes de iniciar listener: {earlySnr}");
                    LaunchCycle?.Invoke(this, new CycleDataModel
                    {
                        SNR = earlySnr,
                        SlotId = _slotId.ToInt(),
                        StationId = _stationData.StationId.ToInt(),
                        DeviceTimeStamp = DateTime.Now
                    });
                    return;
                }

                // Ahora que el receptor está preparado, registramos
                bool shouldLaunchPopup = SerialRouter.TryRegister(_slotId, this, _stationData.StationId.ToString(), _readerMode);

                try
                {
                    // Lanzar el popup si corresponde
                    if (shouldLaunchPopup)
                    {
                        Log.Information($"[{_slotId}] Lanzando popup de lectura...");

                        var promptRequest = new PromptRequest
                        {
                            Type = ePromptType.InputSerial,
                            Title = $"Scan SerialNumber for Slot {_slotId}",
                            Origin = $"{_stationData.StationId.ToString()} - Slot {_slotId}",
                            Payload = new PromptPayloadAction
                            {
                                MainText = $"Scan SerialNumber for Slot {_slotId}",
                                DeviceIdentifier = _slotId,
                                SlotCount= _stationData.SlotCount,
                                ReadingMode = _readerMode,
                                SlotDatalogicIP = _slot.Slot.Datalogic_IP,
                                SlotDatalogicPort = _slot.Slot.Datalogic_Port.ToInt()
                            }
                        };

                        PromptDispatcher.RequestPrompt(promptRequest);
                    }

                    // Esperar el serial vendra del SerialRoute
                    string serial = await _serialTcs.Task;

                    Log.Information($"[{_slotId}] SNR recibido: {serial}");

                    LaunchCycle?.Invoke(this, new CycleDataModel
                    {
                        SNR = serial,
                        SlotId = _slotId.ToInt(),
                        StationId = _stationData.StationId,
                        DeviceTimeStamp = DateTime.Now
                    });
                }
                catch (OperationCanceledException)
                {
                    Log.Information("[{Slot}] Listener cancelado", _slotId);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[{Slot}] Error en StartListening", _slotId);
                }
            }, _cts.Token);
        }

        /// <summary>
        /// Hemos recibido un Serial Number (SNR) del lector.
        /// </summary>
        /// <param name="snr"></param>
        public void ReceiveSerial(string snr)
        {
            _serialTcs?.TrySetResult(snr);
        }
        /// <summary>
        /// Para de escuchar el Serial Number (SNR) del lector.
        /// </summary>
        /// <returns></returns>
        public async Task StopListeningAsync()
        {
            _cts.Cancel();
            // Desregistrar el receptor del SerialRouter
            SerialRouter.Unregister(_slotId);

            if (_readingTask != null)
            {
                try
                {
                    await Task.WhenAny(_readingTask, Task.Delay(5000)); // Espera 5s como máximo
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[BARCODE] Error esperando el fin de lectura");
                }
            }
        }
        /// <summary>
        /// Para de escuchar el Serial Number (SNR) del lector.
        /// </summary>
        public void StopListening()
        {
            _cts.Cancel();
            SerialRouter.Unregister(_slotId);
            _readingTask?.Wait(TimeSpan.FromSeconds(5));
        }
        /// <summary>
        /// Parar de escuchar el Serial Number (SNR) del lector y liberar recursos.
        /// </summary>
        public void Dispose() => StopListening();
    }
}