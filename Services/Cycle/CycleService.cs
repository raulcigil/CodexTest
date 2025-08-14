using Org.BouncyCastle.Bcpg.Sig;
using Serilog;
using System.Collections.ObjectModel;
using TestPlan.Entities.Enumeraciones;
using TestPlan.Entities.Extensions;
using TestPlan.Entities.JSONParameters.ConfigSAPV2;
using TestPlan.Entities.Views;
using TestPlan.Logic.Extensions;
using TestPlan.Logic.Models;
using TestPlan.Logic.Models.UI;
using static Python.Runtime.TypeSpec;

namespace TestPlan.Logic.Services.Cycle
{
    /// <summary>
    /// Servicio que implementa la lógica de ejecución de un ciclo de test
    /// </summary>    
    public class CycleService : IDisposable
    {
        #region Properties
        /// <summary>
        /// Token de cancelación
        /// </summary>
        CancellationTokenSource _cts = new CancellationTokenSource();
        /// <summary>
        /// Token de cancelación
        /// </summary>
        CancellationToken _token;
        /// <summary>
        /// Test en marcha?
        /// </summary>
        public bool _isRunning;
        /// <summary>
        /// Evento que se lanza al iniciar
        /// </summary>
        public event EventHandler<CycleDataModel> CycleStarted = delegate { };
        /// <summary>
        /// Evento que se lanza al terminar 
        /// </summary>
        public event EventHandler<CycleDataModel> CycleCompleted = delegate { };
        /// <summary>
        /// Evento que se lanza al cancelar el ciclo 
        /// </summary>
        public event EventHandler<CycleDataModel> CycleCancelled = delegate { };
        /// <summary>
        /// Datos de ejecución del ciclo
        /// </summary>
        private CycleDataModel _cycleData;
        /// <summary>
        /// Datos de ejecución del ciclo
        /// </summary>
        private StationModel _stationData;
        /// <summary>
        /// Obtener la lista de acciones
        /// </summary>
        private List<PlanActionView> _planActions;
        /// <summary>
        /// Array de los rendezvous que han llegado.
        /// </summary>
        private bool[] completedThreads;
        /// <summary>
        /// Array con los hilos de ejecución
        /// </summary>
        private CycleThread[] _cycleThreads;
        /// <summary>
        /// Tiempo de inicio del ciclo
        /// </summary>
        public DateTime StartTime = DateTime.Now;
        /// <summary>
        /// Tiempo de inicio del ciclo
        /// </summary>
        public DateTime EndTime = DateTime.Now;
        /// <summary>
        /// Resultado del ciclo
        /// </summary>
        private eDefinitionResult _currentResult = eDefinitionResult.NOK;
        /// <summary>
        /// Slot de ejecución
        /// </summary>
        private SlotModel _slotConfig = new SlotModel();
        /// <summary>
        /// Lógica para gestionar los rendezvous
        /// </summary>
        private RendezVousLogic _rendezVousLogic = new RendezVousLogic();
        #endregion

        #region Constructors
        /// <summary>
        /// Constructor
        /// </summary>
        public CycleService(CycleDataModel cycleData, List<PlanActionView> actions, SlotModel slot, StationModel stationData)
        {
            _slotConfig = slot;
            _token = _cts.Token;
            _cycleData = cycleData;
            _stationData = stationData;
            _planActions = actions;
            completedThreads = Array.Empty<bool>();
            _cycleThreads = Array.Empty<CycleThread>();
        }
        #endregion

        #region Cycle Function 
        /// <summary>
        /// Starts the test cycle for a given test plan.
        /// </summary>
        /// <param name="testPlanId">The ID of the test plan to execute.</param>
        /// <returns>True if the test cycle started successfully, false otherwise.</returns>        
        public void StartCycle()
        {
            if (_isRunning || _cts.IsCancellationRequested)
                return;


            //registramos el ciclo en el registro de slot-ciclos
            SlotCycleRegistry.Register(_cycleData.SlotId,this);
            
                // Inicializar variables
            _cycleData.Variables.RefreshFromDatabase(_cycleData);

            StartTime = DateTime.Now;
            _cycleData.CycleStartTime = StartTime;
            _isRunning = true;

            OnCycleStarted(_cycleData);

            //Montar las listas de acciones por hilo max 5 hilos
            int threadCount = 5;
            List<PlanActionView>[] actionsPlanViews = new List<PlanActionView>[threadCount];
            int maxThreadCount = 0;
            for (int i = 0; i < threadCount; i++)
            {
                actionsPlanViews[i] = _planActions.FindAll(x => x.ID_TestPlan_Branch == (i + 1));
                if (actionsPlanViews[i].Count > 0)
                {
                    maxThreadCount++;
                }
                else
                {
                    break;
                }
            }
            //Hilos terminados
            completedThreads = new bool[maxThreadCount];
            //Hilos que ponemos en marcha
            _cycleThreads = new CycleThread[maxThreadCount];
            Task[] tasks = new Task[maxThreadCount];

            for (int i = 0; i < maxThreadCount; i++)
            {
                if (!_cts.IsCancellationRequested)
                {
                    //Cogemos las acciones para este hilo
                    var viewList = actionsPlanViews[i];
                    //Lista observable de acciones.
                    var planActionList = new ObservableCollection<PlanActionModel>();

                    //Creamos instancias con las implementaciones de las acciones especificas
                    foreach (var view in viewList)
                    {
                        var action = PlanActionModel.GetSpecificAction(view, _cycleData, i + 1, _slotConfig,_stationData);
                        if (action != null)
                            planActionList.Add(action);
                    }
                    //Creamos el hilo
                    var thread = new CycleThread(_cycleData, planActionList, _cts, i + 1, _slotConfig,_stationData);
                    _cycleThreads[i] = thread;

                    //Adjuntamos los eventos
                    thread.ThreadCompleted += Thread_Completed;
                    thread.ThreadStarted += Thread_Started;
                    thread.RendezVousEvent += Thread_RendezVousEvent;
                    thread.ThreadAbortActionEvent += Thread_AbortActionEvent;

                    NotifyTestPlanLoaded(_cycleData.StationId, _cycleData.SlotId, planActionList, thread.ThreadId);

                    //Localizamos los rendezvous en este hilo
                    _rendezVousLogic.Search(planActionList, thread.ThreadId);

                    //Ponemos en marcha el hilo
                    tasks[i] = Task.Run(() => thread.StartThreadAsync(), _token);
                }
            }

            if (!_cts.IsCancellationRequested)
            {
                try
                {
                    //Tarea para esperar a que todas las tareas se completen
                    //Indicamos que el ciclo terminó.

                    Task.WhenAll(tasks).ContinueWith(task =>
                    {
                        if (task.IsFaulted)
                        {
                            foreach (var ex in task.Exception?.InnerExceptions)
                            {
                                Log.Error(ex, "Error en el ciclo.");
                            }
                        }

                        if (task.IsCompleted)
                        {
                            EndTime = DateTime.Now;
                            OnCycleCompleted(_cycleData);
                        }
                        else
                        {
                            Log.Error("Error al completar las tareas");
                        }

                        _isRunning = false;
                    }, _token);
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("One or more tasks were canceled.");
                }
                catch (Exception ex)
                {
                    throw ex;
                }
            }
        }

        /// <summary>
        /// Lanza el evento OnStarted
        /// </summary>
        /// <param name="e"></param>
        protected virtual void OnCycleStarted(CycleDataModel e)
        {
            CycleStarted?.Invoke(this, e);
        }
        /// <summary>
        /// Lanza el evento OnCompleted
        /// </summary>
        /// <param name="e"></param>
        protected virtual void OnCycleCompleted(CycleDataModel e)
        {
            //Marcamos el fin del ciclo
            EndTime = DateTime.Now;

            ExportData();
            ExportDataSAP();

            //Notificación MSMQ a SAP
            MSMQExport msMQExport = new MSMQExport();
            //TODO:RAUL: Notificación a MSMQ para enviar datos a SAP

            //Notificamos al main service
            CycleCompleted?.Invoke(this, e);
        }
        /// <summary>
        /// Calcula el resultado para todo el ciclo, recorremos todos los hilos completados
        /// </summary>
        /// <returns>Devuelve true si todos los hilos fueron completados con OK</returns>
        private eDefinitionResult CalculateResult()
        {
            if (_currentResult == eDefinitionResult.ABORTED || _currentResult == eDefinitionResult.EMERGENCY_ABORT)
            {
                return _currentResult;
            }
            bool allTrue = _cycleThreads.All(x => x.CalculateResult() == eDefinitionResult.OK);
            return allTrue ? eDefinitionResult.OK : eDefinitionResult.NOK;
        }
        #endregion

        #region Thread Events
        /// <summary>
        /// Evento que se recibe de un hilo con la acción de abortar
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        /// <exception cref="NotImplementedException"></exception>
        private void Thread_AbortActionEvent(object? sender, bool e)
        {
            Log.Debug($"Abort cycle.(ABORT ACTION)");
            //Recorrer los hilos y llamar a Abort()?
            //¿Marcar el resultado del device como Abort?
            _currentResult = eDefinitionResult.ABORTED;
            _cts.Cancel();
            StopCycle();
            OnCycleCompleted(_cycleData);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="threadId"></param>
        /// <exception cref="NotImplementedException"></exception>
        private void Thread_RendezVousEvent(object? sender, PlanActionModel action)
        {
            _rendezVousLogic.ActivateRendezVousAction(action, _cycleThreads);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Thread_Started(object? sender, EventArgs e)
        {
            CycleThread cycleThread = (CycleThread)sender!;
            //Log.Information("Thread_Started {@ThreadId}", cycleThread.ThreadId);
            Log.Information($"Thread_Started {_cycleData.ToLogInfo(cycleThread.ThreadId)}");
            //Log.Warning<CycleDataModel>("{@data}",_cycleData);
        }
        /// <summary>
        /// Evento que se recibe al terminar un hilo (es posible que queden más hilos en marcha)
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        /// <exception cref="NotImplementedException"></exception>
        private void Thread_Completed(object? sender, EventArgs e)
        {
            CycleThread cycleThread = (CycleThread)sender!;
            completedThreads[cycleThread.ThreadId - 1] = true;
            Log.Information($"Thread_Completed {_cycleData.ToLogInfo(cycleThread.ThreadId)}");

            //bool allTrue = completedThreads.All(x => x);
            //// Miramos si existen mas threads en el test que esten lanzando, sino terminara el ciclo.
            //if (allTrue)
            //{
            //    OnCycleCompleted(_cycleData);
            //}
        }

        #endregion

        #region ExportData / Save Data
        /// <summary>
        /// Exportar los datos del ciclo tal cual estén, da igual si ha fallado el ciclo
        /// </summary>
        private void ExportData()
        {
            bool exportData = true;
            //---------------------------------- EXPORTAR DATOS
            if (exportData)
            {
                //Calcular el resultado final y actualizar
                using HistoryOverviewLogic historyLogic = new HistoryOverviewLogic();
                using ConfigVariablesLogic varsLogic = new ConfigVariablesLogic();

                using HistoryRuntimevariableLogic runtimeVariableLogic = new HistoryRuntimevariableLogic();
                eDefinitionResult finalResult = eDefinitionResult.NOK;

                //Calcular el resultado 
                finalResult = CalculateResult();


                //ACTUALIZAR FINAL RESULT                    
                bool isManualTestPlan = _cycleData.IsManualTestPlan;
                historyLogic.UpdateDeviceDdbb(_cycleData.StationId, _cycleData.HistoryOverviewId, finalResult, Config.Instance.General.Station.StationType, EndTime, isManualTestPlan);

                //Leer las variables ¿qué tengo que hacer con ellas?
                //Variables
                //List<ConfigVariableView> variables = varsLogic.GetConfigvariables(_cycleData.HistoryOverviewId);

                //InsertChilddeviceDdbb ????
                historyLogic.InsertChilddeviceDdbb(_cycleData.HistoryOverviewId, "", "", "", "");

                //PROFILER = EXEC	[dbo].[Save_Overview_Key_Value]

                //TODO:GO - Deshabilitacion de export para depurar sin errores
                //ExportData Runtime variables
                List<ConfigVariableView> exportables = _cycleData.Variables.GetExportableVariables();
                //runtimeVariableLogic.ExportVariables(exportables, _cycleData.HistoryOverviewId);



                //---RESULTADO FINAL----???
                eDefinitionResult resultDevice = historyLogic.GetResultDevice(_cycleData.HistoryOverviewId);
                //eDefinitionResult resultDevice = historyLogic.GetResultDevice(_cycleData.DeviceOverView.SNR);

                //-----------Exportar variables
                //KeyNameValuePairListJson keys = new KeyNameValuePairListJson();
                //keys.KeyNameValues = new List<KeyNameValuePairJson>();
                //foreach (ConfigVariableView item in variables)
                //{
                //    if (item.Exportanable)
                //    {
                //        KeyNameValuePairJson keyData = new KeyNameValuePairJson();
                //        keyData.KeyName = item.Name;
                //        keyData.Value = item.Value;
                //        keyData.PriorityValue = 0;
                //        keys.KeyNameValues.Add(keyData);
                //    }
                //}
                //string jsonKeys = JsonSerializer.Serialize(keys);
                //ACTUALIZAR LAS VARIABLES PARA EL DEVICE
                //keyValueLogic.SaveOverviewKeyValue(_cycleData.HistoryOverviewId, jsonKeys);
            }
        }
        /// <summary>
        /// Exportación de datos de SAP
        /// </summary>
        /// <exception cref="NotImplementedException"></exception>
        private void ExportDataSAP()
        {
            //Utilizamos la clase de exportación de SAP
            SapExport sapExport = new SapExport();

            //Recuperamos la lista de todas las configuraciónes de exportación de datos
            List<ConfigSAP> list = new List<ConfigSAP>();

            //Recuperamos informacion de todos los hilos del ciclo
            foreach (var thread in _cycleThreads)
            {
                list.AddRange(sapExport.GetExportSAPData(thread));
            }

            foreach (var configSAP in list)
            {
                configSAP.PLANTDCPID = Config.Instance.General.Station.PlantDcpId;
                configSAP.DCPID = Config.Instance.General.Station.DcpId;
                configSAP.SCHICHT = "-- PRocedure que tta hora insercion 3 turnos 6-8";

                //TODO:RAUL: Revisar
                configSAP.LINEID = Config.Instance.General.Config.Linea.ToString();
                configSAP.LINEID = Config.Instance.General.RegisterConfig.SapMaschineId;
                // 

                configSAP.SERIALNR = _cycleData.SNR;
                configSAP.MERKNR = configSAP.MerknrValue.ToString();
                configSAP.MERKTXT = configSAP.MerkNrText;
                configSAP.PRUEFER = "AUTIS";
                configSAP.PRUEFDATUV = _cycleData.DeviceTimeStamp.ToyyyyMMdd();

                //config.PRUEFZEITV = config.PRUEFZEITV;
                //config.SATZART = config.SATZART;
                //config.MESSWERT =  "--Result Table";

                configSAP.MESSEINHEIT = "Result";  //"--SAP Tables";
                //config.BEWERTUNG = "-- REsultado general OK = A / NOK R";
                //TODO:RAUL: liMITES?
                configSAP.LSL = "0.00";
                configSAP.USL = "0.00";

                configSAP.FETXT = "--Definition_Errors_Group_Code";
                configSAP.MASCHINE = Config.Instance.General.RegisterConfig.SapMaschineId;
                configSAP.BEREICH = "EM";

                configSAP.NOTIFICATIONTYPE = configSAP.NOTIFICATIONTYPE;
            }
            //Exportación de todos los datos en transacción 
            sapExport.ExportData(list);
        }
        #endregion

        #region Alarms
        /// <summary>
        /// Avisamos de la alarma para detener el ciclo y exportar
        /// </summary>
        public void AlarmAlert()
        {
            Parallel.ForEach(_cycleThreads, thread => thread.AlarmAlert());
            //foreach (CycleThread thread in cycleThreads)
            //{
            //    thread.AlarmAlert();
            //}

            Log.Debug("Alarm alert <<<<<<<<<<<<<<<<<<<< EMERGENCY ABORT");
            _currentResult = eDefinitionResult.EMERGENCY_ABORT;
            StopCycle();
            OnCycleCompleted(_cycleData);
        }
        #endregion

        #region Stop / Dispose / CancelCycle
        /// <summary>
        /// Detiene la escucha cancelando el token 
        /// </summary>
        public void StopCycle()
        {
            SlotCycleRegistry.Unregister(_cycleData.SlotId);
            Log.Debug("StopService CycleService");
            _cts.Cancel();
        }
        /// <summary>
        /// Lanza el evento de cancelar el ciclo
        /// </summary>
        /// <param name="e"></param>
        protected virtual void OnCycleCanceled(CycleDataModel e)
        {
            SlotCycleRegistry.Unregister(_cycleData.SlotId);
            CycleCancelled?.Invoke(this, e);
        }
        /// <summary>
        /// Cancela el ciclo
        /// </summary>
        public void Dispose()
        {
            SlotCycleRegistry.Unregister(_cycleData.SlotId);
            _cycleThreads = Array.Empty<CycleThread>();
            completedThreads = Array.Empty<bool>();
            _planActions = new List<PlanActionView>();
        }

        /// <summary>
        /// Cancela el ciclo de forma controlada por una señal externa (ej: emergencia).
        /// </summary>
        public void CancelCurrentOperation()
        {
            if (!_isRunning)
                return;

            Log.Warning("⛔ Cancelando ciclo del Slot {_slotConfig.SlotId} por señal externa (emergencia)", _slotConfig.SlotId);

            _currentResult = eDefinitionResult.EMERGENCY_ABORT;

            // Detiene los threads y lanza evento de ciclo completado
            StopCycle();
            OnCycleCompleted(_cycleData);
        }
        #endregion

        #region UI Binding Event
        /// <summary>
        /// Thread action group model for the UI.
        /// </summary>
        public static Action<int, int, ObservableCollection<ThreadActionGroupModel>>? OnTestReady;
        /// <summary>
        /// Notificar al Interfaz de Usuario para mostrar información
        /// </summary>
        /// <param name="stationId"></param>
        /// <param name="slotId"></param>
        /// <param name="plan"></param>
        /// <param name="threadId"></param>
        private static void NotifyTestPlanLoaded(int stationId, int slotId, ObservableCollection<PlanActionModel> plan, int threadId)
        {
            if (OnTestReady == null) return;

            var model = new ThreadActionGroupModel
            {
                ThreadId = threadId,
                ThreadName = $"Thread {threadId}",
                Actions = new ObservableCollection<PlanActionModel>(plan.ToList())
            };

            OnTestReady.Invoke(stationId, slotId, new ObservableCollection<ThreadActionGroupModel> { model });
        }
        #endregion 

    }
}
