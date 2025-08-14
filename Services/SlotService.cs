using Microsoft.IdentityModel.Tokens;
using Serilog;
using System.Collections.Concurrent;
using TestPlan.Entities.Enumeraciones;
using TestPlan.Entities.Views;
using TestPlan.Logic.Configuration;
using TestPlan.Logic.ContinuosReading.Eventos.Aggregator;
using TestPlan.Logic.ContinuosReading.Eventos.Messages;
using TestPlan.Logic.ContinuousReading;
using TestPlan.Logic.ContinuousReading.Communication;
using TestPlan.Logic.Extensions;
using TestPlan.Logic.Interfaces;
using TestPlan.Logic.Models;
using TestPlan.Logic.Services.Cycle;
using TestPlan.Logic.Services.IdentifyMode;
using TestPlan.Logic.Services.Monitoring;
using TestPlan.Logic.Services.StartCycle;

namespace TestPlan.Logic.Services
{
    public class SlotWorker : IDisposable
    {
        #region Properties
        /// <summary>
        /// Datos de la estación para propagarlos a los slots, acciones y demás componentes.
        /// </summary>
        private StationModel _stationData;
        /// <summary>
        /// Slot configuration for this station.
        /// </summary>
        private SlotModel? _slot;        
        /// <summary>
        ///     ID of the slot.
        /// </summary>
        private readonly int _slotId;
        /// <summary>
        /// Cantidad de slots en esta estación.
        /// </summary>
        private readonly int _slotStationCount;
        /// <summary>
        /// Event to prompt the user (used by UI).
        /// </summary>
        public event EventHandler<PromptUserData> OpenPromptUserEvent = delegate { };
        /// <summary>
        /// Currently active cycle service for this station.
        /// </summary>
        CycleService? _cycle;
        /// <summary>
        /// Cancellation token source for stopping the station loop.
        /// </summary>
        private CancellationTokenSource _cts = new CancellationTokenSource();
        /// <summary>
        /// Trigger listener service that listens for test cycle start events.
        /// </summary>
        IStartCycleTrigger? _triggerListener;
        /// <summary>
        /// Blocking collection to handle actions from the UI or other sources.
        /// </summary>
        private readonly BlockingCollection<Action> _externalActions = new();
        #endregion

        #region Constructor
        /// <summary>
        /// Constructor for SlotWorker.
        /// </summary>
        /// <param name="stationId">Station Id</param>
        /// <param name="slotId">Slot Id</param>
        /// <param name="slotStationCount">Slot Station Count</param>
        public SlotWorker(StationModel stationData, int slotId)
        {
            _stationData = stationData;            
            _slotId = slotId;
        }
        #endregion

        #region Functions Start / Run
        public void Start()
        {
            StartExternalActionLoop(); // This will handle actions from the UI or other sources.
            Task.Run(() => Run(_cts.Token));
        }
        private void StartExternalActionLoop()
        {
            Task.Run(() =>
            {
                foreach (var action in _externalActions.GetConsumingEnumerable())
                {
                    try { action(); }
                    catch (Exception ex) { Log.Error(ex, $"Error en acción externa en Slot {_slotId}"); }
                }
            });
        }
        private async Task Run(CancellationToken token)
        {
            GlobalStationMonitor.Instance.StationMonitor.UpdateSlotStatus(_stationData.StationId, _slotId, SlotModel.SlotState.Waiting, "Esperando trigger...");

            while (!token.IsCancellationRequested)
            {
                try
                {
                    #region Configuración del Slot
                    //CONFIGURAMOS EL SLOT DE ESTA ESTACION ANTES DE INICIAR EL CICLO lo necesitoamos para ver configuraciones de DB o de IP del TCP 
                    using ConfigSlotsLogic slotLo = new ConfigSlotsLogic();
                    _slot = slotLo.GetConfigSlot(_slotId);
                    Log.Information($"[SlotWorker {_slotId}] Creando nuevo SlotModel: ManagedThreadId={Thread.CurrentThread.ManagedThreadId}, SlotModel hash={_slot.GetHashCode()}");

                    #endregion

                    #region Inicialización Módulos ciclicos
                    // 1. Inicializar el monitor cíclico de la estación esperando poder hacer uso de el 
                    var eventAggregator = SimpleEventAggregator.Instance;
                    // 2. Inicia el runtime (lectores)
                    var runtime = new ContinuosReadingRuntime(eventAggregator);
                    // 3. Configuramos tantos devices por slot como lo que configure web -- Esto seria en el caso que se quiera usar la misma configuracion en diferentes Sltos
                    //foreach (var item in AppConfig.CyclicDevicesConfig)
                    //{
                    //    item.DeviceId = _slot.SlotName;
                    //}
                    // 4. Inicializar los dispositivos cíclicos de la estación
                    await runtime.InitializeFromConfigAsync(AppConfig.CyclicDevicesConfig, _slot);
                    #endregion

                    #region Inicialización de la estación mirar el trigger mode
                    // Determine trigger mode
                    eStartCycleTriggerMode mode = Config.Instance.TriggerMode;
                    if (AppConfig.TestPlan.CmdTriggerMode != -1)
                    {
                        mode = (eStartCycleTriggerMode)AppConfig.TestPlan.CmdTriggerMode;
                    }

                    //TODO:GO - DEBUG que hay que revisar
                    if (AppConfig.TestPlan.BypassPLCReadyCheck)
                    {
                        //Create apropiate listener
                        _triggerListener = mode switch
                        {
                            eStartCycleTriggerMode.PLC => new PLCStartCycleListener(_stationData.StationId, _slotId),
                            eStartCycleTriggerMode.PLC_AND_READER => new WaitFiveStartCycleListener(_stationData.StationId, _slotId),
                            eStartCycleTriggerMode.BARCODE_READER => new BARCODEStartCycleListener(_stationData, _slot),
                            eStartCycleTriggerMode.DEBUG_TRIGGER => new WaitFiveStartCycleListener(_stationData.StationId, _slotId),
                            _ => new WaitFiveStartCycleListener(_stationData.StationId, _slotId)
                        };

                        // Suscribirse al evento ANTES de hacer cualquier otra cosa
                        _triggerListener.LaunchCycle += ExecuteCycleAsync;
                        Log.Debug($"[SLOT {_slotId}] Subscribed to LaunchCycle");

                        // Asegurar que el listener no lanza el ciclo en el constructor o al iniciar el Task inmediatamente
                        await Task.Delay(50); // pequeño delay para garantizar que todo se ha inicializado

                        // Lanzar el listener solo después
                        _triggerListener.StartListening();
                        Log.Debug($"[SLOT {_slotId}] Listener started");

                        // Wait indefinitely until cancellation
                        await Task.Delay(Timeout.Infinite, token);
                    }
                    #endregion
                }
                catch (TaskCanceledException)
                {
                    Log.Error($"Cancelación del ciclo");
                    GlobalStationMonitor.Instance.StationMonitor.UpdateSlotStatus(_stationData.StationId, _slotId, SlotModel.SlotState.Stopped, "Cancelled");
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error($"Error setting slot up in SlotService {ex}");
                    //ErrA00029
                    GlobalStationMonitor.Instance.StationMonitor.UpdateSlotStatus(_stationData.StationId, _slotId, SlotModel.SlotState.Error, ex.Message);
                    await Task.Delay(5000, token); // Retry delay
                }

            }
        }
        #endregion

        #region Cycle Control
        /// <summary>
        /// Called when a trigger signals a new test cycle.
        /// </summary>
        private async void ExecuteCycleAsync(object? sender, CycleDataModel startCycleData)
        {
            //Log.Information($"[SLOT {_slotId}] ExecuteCycleAsync started with SNR: {startCycleData.SNR}");
            GlobalStationMonitor.Instance.StationMonitor.UpdateSlotStatus(_stationData.StationId, _slotId, SlotModel.SlotState.Running, $"Cycle started With SNR {startCycleData.SNR}");

            //Lanzamos el evento de inicializacion de lecturas de modulo ciclico
            //TODO : GO SOlo se inicializa si tiene variables dentro de Ciclico
            SimpleEventAggregator.Instance.Publish(new StartMonitoringEvent
            {
                TargetDeviceType = "OPCUA",
                TargetDeviceId = _slot?.SlotName,
                Parameters = { }
            });


            //Detenemos el modo lectura del SNR para reste Hilo
            if (_triggerListener is BARCODEStartCycleListener barcodeListener)
            {
                await barcodeListener.StopListeningAsync();
            }
            else
            {
                _triggerListener?.StopListening(); // fallback en otros casos
            }

            bool ignoreCycleMode = false;

            try
            {
                CycleDataModel cycleData = new();
                eDefinitionResult deviceResult = eDefinitionResult.WORKING;
                eIdentifyMode mode = Config.Instance.IdentifyMode;

                if (AppConfig.TestPlan.CmdIdentifyMode != -1)
                    mode = (eIdentifyMode)AppConfig.TestPlan.CmdIdentifyMode;

                // Determine identification mode
                if (startCycleData.IsDummy || Config.Instance.CycleMode.IsAutoModeCycleMode())
                {
                    if (!AppConfig.TestPlan.BypassSNR)
                    {
                        switch (mode)
                        {
                            case eIdentifyMode.Undefined:
                                try
                                {
                                    var debugIdentify = new DebugIdentifyMode(startCycleData);
                                    var identifyData = await debugIdentify.IdentifyAsync();
                                    startCycleData.SNR = identifyData.SNR;
                                }
                                catch (Exception e)
                                {
                                    deviceResult = eDefinitionResult.DEVICE_IDENTIFICATION_ERROR;
                                    Log.Error(e, "Undefined (DebugIdentifyMode) DEVICE_IDENTIFICATION_ERROR");
                                    //ErrA00030
                                    _triggerListener?.StartListening();
                                    return;
                                }
                                break;

                            case eIdentifyMode.BARCODE:
                                try
                                {
                                    var barcode = new BarcodeIdentifyMode(startCycleData);
                                    var identifyData = await barcode.IdentifyAsync();
                                    startCycleData.SNR = identifyData.SNR;
                                }
                                catch (Exception e)
                                {
                                    deviceResult = eDefinitionResult.DEVICE_IDENTIFICATION_ERROR;
                                    Log.Error(e, "BarcodeIdentifyMode DEVICE_IDENTIFICATION_ERROR");
                                    //ErrA00031
                                    _triggerListener?.StartListening();
                                    return;
                                }
                                break;

                            case eIdentifyMode.POPUP:
                                try
                                {
                                    //var popup = new PromptUserIdentifyMode(startCycleData);
                                    //var identifyData = await popup.IdentifyAsync();
                                }
                                catch (Exception e)
                                {
                                    deviceResult = eDefinitionResult.DEVICE_IDENTIFICATION_ERROR;
                                    Log.Error(e, "PromptUserIdentifyMode DEVICE_IDENTIFICATION_ERROR");
                                    //ErrA00032
                                    _triggerListener?.StartListening();
                                    return;
                                }
                                break;

                            case eIdentifyMode.LINE_CONTROL:
                                try
                                {
                                    //var lineControl = new LineControlIdentifyMode(startCycleData);
                                    //var identifyData = await lineControl.IdentifyAsync();
                                }
                                catch (Exception e)
                                {
                                    deviceResult = eDefinitionResult.DEVICE_IDENTIFICATION_ERROR;
                                    Log.Error(e, "LineControlIdentifyMode DEVICE_IDENTIFICATION_ERROR");
                                    //ErrA00033
                                    _triggerListener?.StartListening();
                                    return;
                                }
                                break;

                            case eIdentifyMode.PLC_RFID:
                                try
                                {
                                    //var plcRfid = new PlcIdentifyMode(startCycleData);
                                    //var identifyData = await plcRfid.IdentifyAsync();
                                }
                                catch (Exception e)
                                {
                                    deviceResult = eDefinitionResult.DEVICE_IDENTIFICATION_ERROR;
                                    Log.Error(e, "PlcIdentifyMode DEVICE_IDENTIFICATION_ERROR");
                                    //ErrA00034
                                    _triggerListener?.StartListening();
                                    return;
                                }
                                break;

                            case eIdentifyMode.GET_VARIANT_SEQUENCE:
                                try
                                {
                                    //var variant = new VariantSequenceIdentifyMode(startCycleData);
                                    //var identifyData = await variant.IdentifyAsync();
                                }
                                catch (Exception e)
                                {
                                    deviceResult = eDefinitionResult.DEVICE_IDENTIFICATION_ERROR;
                                    Log.Error(e, "VariantSequenceIdentifyMode DEVICE_IDENTIFICATION_ERROR");
                                    //ErrA00035
                                    _triggerListener?.StartListening();
                                    return;
                                }
                                break;

                            case eIdentifyMode.LINE_CONTROL_WITH_READY:
                                try
                                {
                                    // var lineReady = new LineControlWithReadyIdentifyMode(startCycleData);
                                    //var identifyData = await lineReady.IdentifyAsync();
                                }
                                catch (Exception e)
                                {
                                    deviceResult = eDefinitionResult.DEVICE_IDENTIFICATION_ERROR;
                                    Log.Error(e, "LineControlWithReadyIdentifyMode DEVICE_IDENTIFICATION_ERROR");
                                    //ErrA00036
                                    _triggerListener?.StartListening();
                                    return;
                                }
                                break;

                            case eIdentifyMode.READER_NO_POPUP:
                                try
                                {
                                    //var reader = new ReaderNoPopupIdentifyMode(startCycleData);
                                    //var identifyData = await reader.IdentifyAsync();
                                }
                                catch (Exception e)
                                {
                                    deviceResult = eDefinitionResult.DEVICE_IDENTIFICATION_ERROR;
                                    Log.Error(e, "ReaderNoPopupIdentifyMode DEVICE_IDENTIFICATION_ERROR");
                                    //ErrA00037
                                    _triggerListener?.StartListening();
                                    return;
                                }
                                break;

                            case eIdentifyMode.DUMMY:
                                // Ya manejado fuera del switch
                                break;

                            default:
                                Log.Warning("Modo de identificación no reconocido: {Mode}", mode);
                                deviceResult = eDefinitionResult.DEVICE_IDENTIFICATION_ERROR;

                                _triggerListener?.StartListening();
                                return;
                        }
                    }
                    else
                    {
                        if (startCycleData.SNR.IsNullOrEmpty())
                        {
                            startCycleData.SNR = AppConfig.TestPlan.SNR;
                        }
                    }
                }
                else if (Config.Instance.CycleMode.IsForceOrderCycleMode())
                {
                    try
                    {
                        var forceOrder = new ForceOrderIdentifyMode(startCycleData);
                        var identifyData = await forceOrder.IdentifyAsync();
                        startCycleData.SNR = identifyData.SNR;
                        ignoreCycleMode = true;
                    }
                    catch (Exception e)
                    {

                        deviceResult = eDefinitionResult.DEVICE_IDENTIFICATION_ERROR;
                        Log.Error(e, "ForceOrder DEVICE_IDENTIFICATION_ERROR");
                        //ErrA00038
                        _triggerListener?.StartListening();
                        return;
                    }
                }

                // PLAN + History DB Logic
                using var planLogic = new PlanLogic();
                using var hoLogic = new HistoryOverviewLogic();
                using var detailActionLogic = new HistoryDetailActionLogic();

                try
                {                    
                    string _vib = "";

                    cycleData.ORDERNR = planLogic.GetOrderWithSNR(startCycleData.SNR, startCycleData.IsDummy, startCycleData.StationId, ignoreCycleMode, out _vib);
                    //cycleData.SubModelId = planLogic.GetSubmodelId(cycleData.ORDERNR, out _modelID);
                    cycleData.SNR = startCycleData.SNR;
                    cycleData.StationId = startCycleData.StationId;
                    cycleData.SlotId = startCycleData.SlotId;
                    //cycleData.ModelId = _modelID;
                    cycleData.VIB = _vib;
                }
                catch (Exception ex)
                {
                    //TODO:GO Lanzar un pop up de error de identificacion de SNR
                    deviceResult = eDefinitionResult.DEVICE_IDENTIFICATION_ERROR;
                    GlobalStationMonitor.Instance.StationMonitor.UpdateSlotStatus(_stationData.StationId, _slotId, SlotModel.SlotState.Running, $"Device Error identify for SNR: {startCycleData.SNR}");
                    // ErrA00039
                }

                // TODO:GO - Save cycle to DB (Mirar porque tenemos dos valores a piñon )
                if (Config.Instance.General.Station.StationType == eStationType.Single)
                {
                    try
                    {
                        cycleData.HistoryOverviewId = hoLogic.InsertDeviceDdbb_SingleType(cycleData.StationId,
                                                   cycleData.SNR,
                                                   cycleData.VIB,
                                                   cycleData.ORDERNR,                                                   
                                                   cycleData.DeviceTimeStamp,
                                                   "PRE",
                                                   "SIMULATED SNR",
                                                    cycleData.SlotId,
                                                    0,
                                                   cycleData.StationId);
                        Log.Debug($"InsertDeviceDdbb_SingleType overviewId:{cycleData.HistoryOverviewId}");
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(ex, "DEVICE_IDENTIFICATION_ERROR");
                        //ErrA00040
                        throw ex;
                    }


                    //No hemos podido obtener los datos para el SNR
                    if (deviceResult == eDefinitionResult.DEVICE_IDENTIFICATION_ERROR)
                    {
                        //Actualizar el estado, no hay plan para ejecutar NO LANZAMOS CICLO
                        hoLogic.UpdateDeviceDdbb(cycleData.StationId, cycleData.HistoryOverviewId, eDefinitionResult.DEVICE_IDENTIFICATION_ERROR, cycleData.StationType, DateTime.Now, cycleData.IsManualTestPlan);
                        _triggerListener?.StartListening();
                        return;
                    }
                }
                try
                {
                    //Obtener el plan para La orden asociada al SNR
                    Guid uUID_TestPlan_Version = Guid.Empty;
                    cycleData.UID_TestPlan_Version = planLogic.GetTestPlanGeneralId(cycleData.StationId, cycleData.ORDERNR);

                    Log.Debug($"InsertDeviceDdbb_SingleType UID_TestPlan_Version:{cycleData.UID_TestPlan_Version}");
                    //if (AppConfig.TestPlan.CmdPlanId != -1)
                    //{
                    //    cycleData.TestPlanID = AppConfig.TestPlan.CmdPlanId;
                    //}
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, $"INVALID_TESTPLAN UID_TestPlan_Version:{cycleData.UID_TestPlan_Version}");
                    //ErrA00041
                    //Actualizar el estado, no hay plan para ejecutar NO LANZAMOS CICLO
                    hoLogic.UpdateDeviceDdbb(cycleData.StationId, cycleData.HistoryOverviewId, eDefinitionResult.INVALID_TESTPLAN, cycleData.StationType, DateTime.Now, cycleData.IsManualTestPlan);
                    return;
                }

                bool degradedStation = planLogic.GetStationDegraded(cycleData.StationId);
                if (degradedStation)
                {
                    //Actualizar el estado , NO LANZAMOS CICLO
                    hoLogic.UpdateDeviceDdbb(cycleData.StationId, cycleData.HistoryOverviewId, eDefinitionResult.DEGRADED, cycleData.StationType, DateTime.Now, cycleData.IsManualTestPlan);
                    return;
                }

                //TODO:GABI:Revisar si es necesario
                //EXEC	[dbo].[Is_Repeated_Device]
                //EXEC	[dbo].[Get_Station_Aborted]
                //EXEC	[dbo].[Get_AutoMode]

                //Obtener la lista de acciones 
                List<PlanActionView> actions = planLogic.GetTestPlanActionsForPlanId(cycleData.StationId, cycleData.ORDERNR, cycleData.UID_TestPlan_Version);

                Log.Information($"GetTestPlanActionsForPlanId UID_TestPlan_Version:{cycleData.UID_TestPlan_Version} stationId:{cycleData.StationId} orderNR:{cycleData.ORDERNR}");

                //TODO:123:BORJA ¿es un device que ya hemos procesado? ¿que hacer? He puesto aquí esta linea pq sale en el SQL Profiler después de GetConfigSlot. Investigar.
                bool isRepeatedDevice = hoLogic.IsRepeatedDevice(cycleData.StationId, cycleData.HistoryOverviewId, cycleData.DeviceTimeStamp, cycleData.SNR);

                if (_slot == null)
                {
                    Log.Debug("Slot Null");
                    //Actualizar el estado, no hay plan para ejecutar NO LANZAMOS CICLO
                    hoLogic.UpdateDeviceDdbb(cycleData.StationId, cycleData.HistoryOverviewId, eDefinitionResult.DEVICE_IDENTIFICATION_ERROR, cycleData.StationType, DateTime.Now, cycleData.IsManualTestPlan);
                    return;
                }

                //Registro al iniciar ciclo - SUEDAT
                using SuedatLogic suedatLogic = new SuedatLogic();
                suedatLogic.InsertDataSuedat(cycleData.DeviceTimeStamp,
                    "DCPID",
                    "UNITID",
                    startCycleData.IsDummy);

                //Iniciamos el ciclo
                _cycle = new CycleService(cycleData, actions, _slot,_stationData);
                //Evento para controla el fin del ciclo 
                _cycle.CycleCompleted += Cycle_Completed;

                //Go!
                Log.Information($"Cycle STARTED {startCycleData.SNR} {cycleData.ToLogInfo()}");
                _cycle.StartCycle();
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Cycle start error in station {_stationData.StationId}");
                //Cerramos el modulo de lecturas ciclico ya ue el ciclo no ha empezado
                SimpleEventAggregator.Instance.Publish(new StopMonitoringEvent
                {
                    TargetDeviceType = "OPCUA",
                    TargetDeviceId = _slot?.SlotName,
                    Parameters = { }
                });
            }
        }

        /// <summary>
        /// Called when the cycle completes.
        /// </summary>
        public void Cycle_Completed(object? sender, CycleDataModel cycleData)
        {
            // Acabamos el ciclo y actualizamos el estado del slot
            Log.Information($"Cycle COMPLETED - SNR:{cycleData.SNR}  Order:{cycleData.ORDERNR} UID_TestPlan_Version:{cycleData.UID_TestPlan_Version}");
            GlobalStationMonitor.Instance.StationMonitor.UpdateSlotStatus(cycleData.StationId, cycleData.SlotId, SlotModel.SlotState.Waiting, "Cycle completed, waiting for next");

            //Cerramos el modulo de lecturas cíclico ya ue el ciclo ha terminado
            SimpleEventAggregator.Instance.Publish(new StopMonitoringEvent
            {
                TargetDeviceType = "OPCUA",
                TargetDeviceId = _slot?.SlotName,
                Parameters = { }
            });

            // Guardamos el resultado del ciclo en la base de datos
            try
            {
                // When the test finish we restart Listener for throw next test
                _triggerListener?.StartListening();
            }
            catch (Exception)
            {
                Log.Information($"Error volviendo a empezar el listener del SLot{_slotId}");
            }


        }
        #endregion

        #region Function Stop 
        public void Stop()
        {
            _triggerListener?.StopListening();
            _cts.Cancel();
        }
        /// <summary>
        /// Disposes of resources and stops the station.
        /// </summary>
        public void Dispose()
        {
            Stop();

        }
        #endregion

        #region Methods to receive action from the UI 
        public void EnqueueExternalAction(Action action)
        {
            if (!_cycle._isRunning)
            {
                _externalActions.Add(action);
            }
        }
        #endregion 

    }
}
