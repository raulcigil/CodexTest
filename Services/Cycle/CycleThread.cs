using Microsoft.Extensions.Options;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Threading;
using TestPlan.Entities;
using TestPlan.Entities.Enumeraciones;
using TestPlan.Entities.Extensions;
using TestPlan.Entities.JSON;
using TestPlan.Entities.JSONActionData;
using TestPlan.Entities.JSONParameters.AfterAction;
using TestPlan.Entities.JSONParameters.AfterConditions;
using TestPlan.Entities.Views;
using TestPlan.Logic.Actions;
using TestPlan.Logic.Extensions;
using TestPlan.Logic.Interfaces;
using TestPlan.Logic.Models;

namespace TestPlan.Logic.Services.Cycle
{
    /// <summary>
    /// Servicio que implementa la ejecución de un hilo de test
    /// </summary>
    public class CycleThread
    {
        #region Properties
        /// <summary>
        /// Manejador de eventos
        /// </summary>
        ManualResetEventSlim _mre = new ManualResetEventSlim(true);
        /// <summary>
        /// Token de cancelación
        /// </summary>
        CancellationTokenSource _cts;
        /// <summary>
        /// Test en marcha?
        /// </summary>
        private bool _isRunning;
        /// <summary>
        /// Alerta de alarma?
        /// </summary>
        private bool _isAlarmAlert = false;
        /// <summary>
        /// Evento que se lanza al iniciar
        /// </summary>
        public event EventHandler? ThreadStarted;
        /// <summary>
        /// Evento que se lanza al terminar 
        /// </summary>
        public event EventHandler? ThreadCompleted;
        /// <summary>
        /// Evento que se lanza con la acción de RendezVous
        /// </summary>
        public event EventHandler<PlanActionModel>? RendezVousEvent;
        /// <summary>
        /// Evento que se lanza con la acción de Abort
        /// </summary>
        public event EventHandler<bool>? ThreadAbortActionEvent;
        /// <summary>
        /// Lista de acciónes para este hilo
        /// </summary>
        private List<PlanActionView> _actionViews;
        /// <summary>
        /// Lista de acciónes para este hilo
        /// </summary>
        private ObservableCollection<PlanActionModel> _actions;
        /// <summary>
        /// List of actions 
        /// </summary>
        internal ObservableCollection<PlanActionModel> Actions { get { return _actions; } set { _actions = value; } }
        /// <summary>
        /// Datos de ejecución del ciclo
        /// </summary>
        private CycleDataModel _cycleData;
        /// <summary>
        /// Datos de la estación
        /// </summary>
        private StationModel _stationData;
        /// <summary>
        /// Slot Id 
        /// </summary>
        private SlotModel _slot;
        /// <summary>
        /// Identificador del hilo
        /// </summary>
        private int _threadId;
        /// <summary>
        /// Identificador del hilo (asignado,propio)
        /// </summary>
        public int ThreadId { get { return _threadId; } }
        /// <summary>
        ///  Acción en ejecución
        /// </summary>
        private PlanActionModel _currentAction;
        /// <summary>
        ///  Indice actual dentro del array de acciones
        /// </summary>
        private int _currentActionIndex;
        #endregion

        #region Constructor
        /// <summary>
        /// Constructor
        /// </summary>
        public CycleThread(CycleDataModel cycleData, ObservableCollection<PlanActionModel> actions, CancellationTokenSource cts, int threadId, SlotModel slot, StationModel stationData)
        {
            _stationData = stationData;
            _slot = slot;
            _threadId = threadId;
            _cts = cts;
            _cycleData = cycleData;
            _currentAction = new PlanActionModel(new PlanActionView());

            _actions = actions; // Asignamos las acciones que nos vienen directamente del Cycle Service para no crear estancias diferentes 
        }
        #endregion

        #region Start / Execute 
        /// <summary>
        /// Ejecutar el hilo
        /// </summary>
        /// <returns></returns>
        public async Task<bool> StartThreadAsync()
        {
            //bool exportData = true;
            DateTime startTime = DateTime.Now;

            if (_isRunning)
                return false;

            _isRunning = true;
            OnThreadStarted(EventArgs.Empty);
            try
            {
                //Recorrer todas las acciones
                for (int i = 0; i < _actions.Count; i++)
                {
                    bool executeAction = true;
                    _currentActionIndex = i;
                    _currentAction = _actions[_currentActionIndex];
                    //Falla hasta que se demuestre lo contrario
                    eResult result = eResult.Failed;

                    if (_currentAction.IsEnabled)
                    {
                        //Al menos se ejecuta una vez
                        _currentAction.MaxRetriesCount = 0;
                        bool continueRetry = true;
                        do
                        {
                            _currentAction.CurrentResult = eDefinitionResult.NOK;
                            //Ejecutamos la acción salvo excepciones
                            executeAction = true;

                            //NO MOVER ESTA LINEA DE SITIO: seteamos el inicio de la accion
                            _currentAction.StartTime = DateTime.Now;
                            //Id del hilo para el rendezvous
                            _currentAction.ThreadId = _threadId;
                            try
                            {
                                //Abortar
                                if (_currentAction.ActionType == eActionType.Abort)
                                {
                                    Log.Verbose($"ABORT ACTION EXECUTED {_cycleData.ToLogInfo(ThreadId)}");
                                    executeAction = false;
                                    _currentAction.CurrentResult = eDefinitionResult.ABORTED;
                                    ActionAbort abort = (ActionAbort)_currentAction;
                                    OnThreadAbortAction(abort.Parameters.AbortAllStations);
                                }
                                //// Mensaje al usuario
                                //if (_currentAction.ActionType == eActionType.PromptUser)
                                //{
                                //    executeAction = true;
                                //    _currentAction.CurrentResult = eDefinitionResult.WORKING;
                                //}

                                //Esperamos a que todos los rendezvous se resuelvan
                                if (_currentAction.ActionType == eActionType.Rendezvous)
                                {
                                    executeAction = false;

                                    //Parar este ciclo
                                    _mre.Reset();

                                    //Id del hilo para el rendezvous
                                    _currentAction.ThreadId = ThreadId;

                                    //Enviamos la acción 
                                    OnRendezVousEvent(_currentAction);
                                }
                                //En algunos casos no vamos a ejecutar un IplanAction

                                //------¿EJECUTAR LA ACCIÓN?
                                if (executeAction)
                                {
                                    //Ejecutar acción
                                    TaskActionResult res = ExecuteAction(_currentAction, _currentActionIndex);

                                    // seteamos el inicio de la accion
                                    _currentAction.EndTime = DateTime.Now;

                                    result = res.Result;

                                    //Saltamos a la acción indicada (after actions)
                                    if (result.IsGotoMark())
                                    {
                                        //Establecemos el nuevo indice en la lista de acciones
                                        i = res.NewIndex - 1;
                                    }

                                }
                            }
                            catch (OperationCanceledException)
                            {
                                _currentAction.CurrentResult = eDefinitionResult.EMERGENCY_ABORT;
                            }
                            catch (Exception ex)
                            {
                                //Puede ser una excepción por token cancelado o no 
                                result = eResult.Failed;
                                Log.Error(ex, _currentAction.GetLogActionName() + " Exception");
                            }
                            //Conteo de intentos de solución
                            _currentAction.MaxRetriesCount++;

                            //¿realmente quiero que se lance la excepcion? - Se controla en el while el token.
                            //_cts.Token.ThrowIfCancellationRequested();                            

                            //Este Wait comprueba el token y lanza excepción
                            //COMPROBAR si el freno de mano en esta acción es un rendezvouz y pararse aquí
                            _mre.Wait(_cts.Token);

                            //¿Continuamos reintentando esta acción?
                            continueRetry = result != eResult.Success;
                            continueRetry = continueRetry && result != eResult.GoToMark;
                            continueRetry = continueRetry && _currentAction.MaxRetriesCount < _currentAction.MaxRetries;
                            continueRetry = continueRetry && !_cts.IsCancellationRequested;
                            if (continueRetry)
                            {
                                _currentAction.CurrentResult = eDefinitionResult.RETRYING;
                            }
                        }
                        // Si falla y hay reintentos continuar intentando
                        while (continueRetry);
                    }
                    else
                    {
                        //Juan indico que si no está habilitada no se ejecutara y será BYPASSED
                        _currentAction.CurrentResult = eDefinitionResult.BYPASSED;
                        Log.Debug(_currentAction.GetLogActionName() + " DISABLED (not executed)");
                    }

                    //---------------------------------------------------------------------------------------UPDATE ACTION---
                    UpdateAction(_currentAction, _currentAction.StartTime, _currentAction.CurrentResult);
                    //Log.Information(_currentAction.GetLogResult());
                    //---------------------------------------------------------------------------------------
                    if (result == eResult.Exception) { break; }
                }//End For: Acciones en la lista
                //
                _currentAction = new PlanActionModel(new PlanActionView());
                _isRunning = false;
                //---------------------------------- FIN DE EJECUCIÓN DE ACCIONES
                //if (actionResult == eDefinitionResult.ABORTED)
                OnThreadCompleted(EventArgs.Empty);
                return true;
            }
            catch (OperationCanceledException)
            {

                _isRunning = false;
                //Actualizar el estado en caso de alarma
                if (_isAlarmAlert && _currentAction != null)
                {
                    UpdateAction(_currentAction, _currentAction.StartTime, eDefinitionResult.EMERGENCY_ABORT);
                    Log.Information("Thread {id} cancelado - EMERGENCY_ABORT", ThreadId);
                }
                else if (_currentAction?.ActionType == eActionType.Abort)
                {
                    UpdateAction(_currentAction, _currentAction.StartTime, _currentAction.CurrentResult);
                    Log.Information("Thread {id} cancelado - ABORT ACTION", ThreadId);
                }
                else
                {
                    Log.Information("Thread {id} cancelado - UNDEFINED", ThreadId);
                }

                return false;
            }
            catch (AggregateException ex)
            {
                _isRunning = false;

                foreach (var e in ex.InnerExceptions)
                {
                    if (e is OperationCanceledException)
                    {
                        Console.WriteLine("Operación cancelada (AggregateException).");
                    }
                    else
                    {
                        Console.WriteLine(e.Message);
                    }
                }
                return false;
            }
        }
        #endregion

        #region History Detail Action
        /// <summary>
        /// Devuelve el string en JSON para almacenar el detalle de la acción en el campo data.
        /// </summary>
        /// <param name="action">Acción con los datos</param>
        /// <returns></returns>
        private string GetJsonDetailType(PlanActionModel action)
        {
            string emptyJson = @"{""saveDetail"":{""ShowDetailType"":""emptyDetail"",""void"":0},""error"":{""status"":false,""code"":0,""source"":""""}}";
            //¿¿Se guarda NULL o se guardar string.Empty??
            if (action.ShowDetailType == eShowDetailType.emptyDetail)
            {
                return emptyJson;
            }
            //Devolver el json con los datos 
            IPlanAction iAction = (IPlanAction)action;

            return iAction.GetShowDetailType() ?? emptyJson;
        }
        /// <summary>
        /// Devuelve el string en JSON para almacenar el detalle de la acción en el campo data.
        /// </summary>
        /// <param name="action">Acción con los datos</param>
        /// <returns></returns>
        public static string GetActionData(PlanActionModel action)
        {
            IPlanAction iAction = (IPlanAction)action;
            ActionDataJson data = iAction.GetActionData() ?? new ActionDataJson();
            return JsonSerializer.Serialize<ActionDataJson>(data);
        }
        #endregion



        /// <summary>
        /// Avisamos de la alarma para detener el ciclo y exportar
        /// </summary>
        public void AlarmAlert()
        {
            //Avisamos de la alarma
            _isAlarmAlert = true;
            //Actualizar el estado en caso de alarma
            if (_currentAction != null)
            {
                //Notificar a la accion de la alarma ya que está en otro hilo y ver si decidimos pararla.
                _currentAction.AlarmAlert();
            }
            //El ultimo paso es cancelar el hilo / acción en curso
            _cts.Cancel();
        }
        /// <summary>
        /// Actualiza el estado de esta acción, y calcula los datos de offset y tiempo total
        /// </summary>
        /// <param name="action"></param>
        private void UpdateAction(PlanActionModel action, DateTime startTime, eDefinitionResult actionResult)
        {
            if (action.IsAfterAction)
            {
                UpdateSubAction(action, startTime, actionResult);
            }
            else
            {
                UpdateNormalAction(action, startTime, actionResult);
            }
        }
        /// <summary>
        /// Actualiza el estado de esta acción, y calcula los datos de offset y tiempo total
        /// </summary>
        /// <param name="action"></param>
        private void UpdateNormalAction(PlanActionModel action, DateTime startTime, eDefinitionResult actionResult)
        {
            //---UPDATE ACTION--- Actualización del resultado de la acción 
            //Termina la acción
            DateTime endTime = DateTime.Now;

            //Tiempos de inicio y fin de la acción 
            action.StartTime = startTime;
            action.EndTime = endTime;

            //Asignamos la acción, por si hay modificaciones posteriores al resultado de ejecutar laacción
            //El resultado de la acción se establece siempre en el momento de la ejecución dentro de la clase de la Acción
            action.CurrentResult = actionResult;

            //Nuevo registro en la tabla History_Detail_Action
            HistoryDetailAction historyDetailAction = new HistoryDetailAction();
            historyDetailAction.ID_History_Overview = _cycleData.HistoryOverviewId;

            historyDetailAction.UID_ConfigParameter_Action_Version = action.UID_ConfigParameter_Action_Version;

            historyDetailAction.ID_Station = _cycleData.StationId;
            historyDetailAction.ID_TestPlanBranch = action.BranchId;
            historyDetailAction.Step = action.Step;
            historyDetailAction.ID_Result = action.CurrentResult;
            historyDetailAction.Total_Time = action.TotaltimeSeconds;

            //Obtener los datos de la acción para adjuntarlos al Overview
            historyDetailAction.Data = GetJsonDetailType(action);
            historyDetailAction.ActionData = GetActionData(action);

            historyDetailAction.TimestampInit = action.StartTime;
            historyDetailAction.Retries = action.MaxRetriesCount;
            historyDetailAction.Ignore_Result = action.IgnoreResult.BoolToInt();
            historyDetailAction.Summary_Action = action.IsSummary.BoolToInt();
            historyDetailAction.SummaryName_Action = action.SummaryName;
            //TODO:RAUL: Eliminar columna de BD
            historyDetailAction.ID_SubStation = 0;
            historyDetailAction.Negate_Result = action.NegateResult.ToInt();

            //Calcular los offsets de tiempo de esta acción en segundos
            float offSetStart = (float)(startTime - _cycleData.CycleStartTime).TotalMilliseconds / (float)1000;
            float offSetEnd = (float)(endTime - _cycleData.CycleStartTime).TotalMilliseconds / (float)1000;
            historyDetailAction.Offset_Start_s = offSetStart;
            historyDetailAction.Offset_End_s = offSetEnd;

            //Insertar el registro en la BD
            using HistoryDetailActionLogic detailActionLogic = new HistoryDetailActionLogic();
            detailActionLogic.Insert(historyDetailAction);

            //ID - Asignamos el registro a la acción para posteriores updates si fuera necesario
            action.HistoryDetailActionId = historyDetailAction.ID_History_Detail_Action;

            //TODO: ----------A CONTINUACIÓN EXPORTACIÓN DE DATOS OverViewKeyValue 
            ExportOverviewKeyValues(action, _cycleData);

            //Enviar a la BD las variables modificadas por esta acción
            action.UpdateConfigRuntimeVariables();
        }
        /// <summary>
        /// Actualiza el estado de esta acción, y calcula los datos de offset y tiempo total
        /// </summary>
        /// <param name="action"></param>
        private void UpdateSubAction(PlanActionModel action, DateTime startTime, eDefinitionResult actionResult)
        {
            //---UPDATE ACTION--- Actualización del resultado de la acción 
            //Termina la acción
            DateTime endTime = DateTime.Now;

            //Tiempos de inicio y fin de la acción 
            action.StartTime = startTime;
            action.EndTime = endTime;

            //Asignamos la acción, por si hay modificaciones posteriores al resultado de ejecutar laacción
            //El resultado de la acción se establece siempre en el momento de la ejecución dentro de la clase de la Acción
            action.CurrentResult = actionResult;

            //Nuevo registro en la tabla History_Detail_Action
            HistoryDetailSubaction hSubAction = new HistoryDetailSubaction();
            // -- Parent Action
            hSubAction.UID_ConfigParameters_Action_Version = action.ParentAction.UID_ConfigParameter_Action_Version;
            hSubAction.ID_History_Detail_Action = action.ParentAction.HistoryDetailActionId;
            hSubAction.ID_TestPlanBranch = action.ParentAction.BranchId;
            hSubAction.Step = action.ParentAction.Step;

            hSubAction.ID_History_Overview = _cycleData.HistoryOverviewId;

            hSubAction.ID_Station = _cycleData.StationId;
            hSubAction.ID_Result = action.CurrentResult;
            hSubAction.Total_Time = action.TotaltimeSeconds;
            //Obtener los datos de la acción para adjuntarlos al Overview
            hSubAction.Data = GetJsonDetailType(action);
            hSubAction.ActionData = GetActionData(action);

            hSubAction.TimestampInit = action.StartTime;
            hSubAction.Retries = action.MaxRetriesCount;
            hSubAction.Ignore_Result = action.IgnoreResult.BoolToInt();
            hSubAction.Summary_Action = action.IsSummary.BoolToInt();
            hSubAction.SummaryName_Action = action.SummaryName;
            hSubAction.Negate_Result = action.NegateResult.ToInt();

            //Calcular los offsets de tiempo de esta acción en segundos
            float offSetStart = (float)(startTime - _cycleData.CycleStartTime).TotalMilliseconds / (float)1000;
            float offSetEnd = (float)(endTime - _cycleData.CycleStartTime).TotalMilliseconds / (float)1000;
            hSubAction.Offset_Start_s = offSetStart;
            hSubAction.Offset_End_s = offSetEnd;

            //Insertar el registro en la BD
            using HistoryDetailSubactionLogic detailActionLogic = new HistoryDetailSubactionLogic();
            detailActionLogic.Insert(hSubAction);

            //ID - Asignamos el registro a la acción para posteriores updates si fuera necesario
            action.HistoryDetailSubActionId = hSubAction.ID_History_Detail_SubAction;
            action.HistoryDetailActionId = hSubAction.ID_History_Detail_Action;

            //TODO: ----------A CONTINUACIÓN EXPORTACIÓN DE DATOS OverViewKeyValue 
            ExportOverviewKeyValues(action, _cycleData);

            //Enviar a la BD las variables modificadas por esta acción
            action.UpdateConfigRuntimeVariables();
        }

        /// <summary>
        /// Exportar los datos configurados en la acción.
        /// </summary>
        /// <param name="cycleData">Datos del ciclo actual</param>
        /// <returns>True si se exportaron los datos correctamente</returns>
        public bool ExportOverviewKeyValues(PlanActionModel action, CycleDataModel cycleData)
        {
            try
            {
                List<OverViewKey> list = new List<OverViewKey>();
                IPlanAction planAction = (IPlanAction)action;
                if (planAction != null && action.ConfigExportDataV2 != null && action.ConfigExportDataV2.Config_ExportData.Count > 0)
                {
                    //Obtener los datos de la acción actual para exportarlos.
                    //La acción es la que sabe los datos que hay que recoger por eso pasamos por la interfaz
                    //Pasamos como parámetro toda la lista de parametros, que aunque está dentro la acción, si lo hacemos así podríamos realizar 
                    //filtros y varias llamadas a listas diferentes. Por eso lo pasamos cómo parámetro Config_ExportData.

                    //TODO:RAUL: Hemos hablado que deberiamos cambiar la forma de exportar los datos
                    //Sería interesante guardar TODOS los datos en un JSON para cada acción y la exportación
                    //realizarla en el momento de exportar el fichero CSV desde la inferfaz. 
                    //La tabla de Export_Overview_Key_Value ¿se realizan filtros o consultas sobre ella?
                    //La explotación de datos sería en otra BD OLAP y no OLTP
                    //La idea sería exportar un string con un JSON para almacenar en la BD (veremos donde y cómo)

                    list.AddRange(planAction.GetOverviewKeyValueList(cycleData, action.ConfigExportDataV2.Config_ExportData));
                    return true;
                }
                else
                    return false;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error exporting data, ExportOverviewKeyValues");
                return false;
            }
        }

        /// <summary>
        /// Ejecuta las acciones post action 
        /// </summary>
        /// <param name="actionModel">La acción ejecutada</param>
        /// <returns></returns>
        private TaskActionResult InsertAfterActions(PlanActionModel actionModel, int currentIndex)
        {
            TaskActionResult result = new TaskActionResult() { Result = eResult.Failed };
            //Es una after action insertada en la lista de acciones, no tiene más after actions, continuamos.
            if (actionModel.IsAfterAction)
            {
                result.Result = eResult.Success;
                return result;
            }

            List<AfterActionJson>? afterActions = null;
            if (actionModel.IsResultSuccess)
            {
                afterActions = actionModel.AfterOKAction;
            }
            else if (actionModel.IsResultFailed)
            {
                afterActions = actionModel.AfterNOKAction;
            }

            //No hay post acciones
            if (afterActions == null || afterActions.Count == 0)
            {
                result.Result = eResult.Success;
                return result;
            }

            //Añadimos las after actions a la accion actual
            using (PlanLogic logic = new PlanLogic())
            {
                int index = currentIndex + 1;
                foreach (AfterActionJson action in afterActions)
                {
                    //Obtener la acción
                    ActionParametersView parametersAction = logic.GetActionParameters(action.UID_ConfigParameters_Action.ToGuid(), _cycleData.ORDERNR, _cycleData.StationId);

                    if (parametersAction != null)
                    {
                        //Modelo de vista de datos de un after action
                        PlanActionView actionView = PlanActionModel.GetPlanActionViewAfterAction(parametersAction, action);
                        //Acción especifica desde el modelo de vista obtenido
                        PlanActionModel afterAction = PlanActionModel.GetSpecificAction(actionView, _cycleData, _threadId, _slot, _stationData);
                        afterAction.ParentAction = actionModel;

                        //Insertamos la acción en el indice actual
                        _actions.Insert(index, afterAction);
                        index++;

                        actionModel.AfterActions.Add(afterAction);
                    }
                }
            }
            result.Result = eResult.Success;
            return result;
        }

        /// <summary>
        /// Ejecuta la acción de tipo DBUS2
        /// </summary>
        /// <param name="action"></param>
        private TaskActionResult ExecuteAction(PlanActionModel action, int currentIndex)
        {
            TaskActionResult taskResult = new TaskActionResult() { Result = eResult.Failed };
            bool isCompleted = false;

            eResult result = eResult.Success;


            IPlanAction planAction = (IPlanAction)action;
            try
            {
                //Lanzamos la tarea
                //PROMPT USER
                if (_isAlarmAlert)
                {
                    action.CurrentResult = eDefinitionResult.EMERGENCY_ABORT;
                    return taskResult;
                }
                else
                {
                    //------------EJECUTAR TAREA                    
                    //Asignar el token para poder comprobarlo dentro de la acción si estamos en un bucle.
                    action.CancellationToken = _cts;
                    Task<eResult> t = Task<eResult>.Run(() => { return planAction.Start(); }, _cts.Token);

                    if (action.ActionType == eActionType.PromptUser || action.TimeOut == 0)
                    {
                        //Esperamos a que se cancele el token de la tarea
                        t.Wait();
                        isCompleted = true;
                    }
                    else
                    {
                        //Esperamos el timeout
                        isCompleted = t.Wait(TimeSpan.FromMilliseconds(action.TimeOut), _cts.Token);
                    }

                    _cts.Token.ThrowIfCancellationRequested();

                    //Comprobar el timeout
                    if (isCompleted && !t.IsFaulted)
                    {
                        taskResult.Result = result = t.Result;

                        //CONDITIONAL GOTO ACTION o GOTO ACTION
                        if (action.ActionType == eActionType.GoTo || action.ActionType == eActionType.ConditionalGoTo)
                        {
                            int newIndex = 0;
                            string goToMark = string.Empty;
                            eGotoMode gotoMode = eGotoMode.Undefined;
                            int gotoStep = 0;
                            switch (action.ActionType)
                            {
                                case eActionType.GoTo:
                                    ActionGoTo actionGoto = (ActionGoTo)action;
                                    goToMark = actionGoto.GoToMark;
                                    gotoMode = actionGoto.GoToMode;
                                    gotoStep = actionGoto.GoToStep;
                                    break;
                                case eActionType.ConditionalGoTo:
                                    ActionConditionalGoto conditionalGoto = (ActionConditionalGoto)action;
                                    goToMark = conditionalGoto.GoToMark;
                                    gotoMode = eGotoMode.Mark;
                                    break;
                            }

                            if (result == eResult.GoToMark)
                            {
                                //Desplazarnos a la nueva posición
                                eResult resultExecuteGoToMark = GoToAction(action, currentIndex, goToMark, _actions, out newIndex, gotoMode, gotoStep);

                                if (resultExecuteGoToMark == eResult.GoToMark)
                                {
                                    //Es un CONDITIONALGOTO vamos a establecer la información de la acción para ShowDetails
                                    if (action.ActionType == eActionType.ConditionalGoTo)
                                    {
                                        ActionConditionalGoto conditionalGoto = (ActionConditionalGoto)action;
                                        conditionalGoto.SetShowDetailInfo(_actions[currentIndex].Step, _actions[newIndex].Step, conditionalGoto.GoToMarkRetriesCount, conditionalGoto.GoToMark);
                                    }
                                    //Nuevo indice en la lista de acciones a ejecutar
                                    taskResult.NewIndex = newIndex;
                                    taskResult.Result = eResult.GoToMark;
                                    return taskResult;
                                }
                                //Pasa por aquí si se han completado los reintentos o si no es posible ir a la etiqueta porque no se encuentra
                                else if (resultExecuteGoToMark == eResult.Success)
                                {
                                    taskResult.Result = eResult.Success;
                                    return taskResult;
                                }
                            }
                        }
                    }
                    else
                    {
                        t.Wait();
                        if (t.IsFaulted || t.Exception != null)
                        {
                            Log.Error(t.Exception, t.Exception.Message);
                        }

                        taskResult.Result = result = eResult.FailedTimeout;
                        action.Timeout();
                    }
                }
            }
            catch (TimeoutException ex)
            {
                isCompleted = false;
                result = eResult.FailedTimeout;
            }
            catch (OperationCanceledException ex)
            {
                isCompleted = false;
                result = eResult.OperationCanceled;
                return taskResult;
            }
            catch (Exception ex)
            {
                isCompleted = false;
                result = eResult.Exception;

                ErrorWindowInAction errWindow = new(new PlanActionView(), ex.InnerException?.Message ?? ex.Message, _cycleData, _stationData);

                taskResult.Result = result;
            }

            //Actualizar el resultado de la acción principal
            //EL RESULTADO SE ACTUALIZA DIRECTAMENTE EN LA ACCION,
            //PERO SI ES UN GRUPO Y ES LA ULTIMA ACCION LO VAMOS A CALCULAR DE NUEVO
            // Aquí se comprueba si es acción ACTION_GROUP y se calcula el resultado final del grupo
            if (action.IsGroup && action.GroupStepId == action.LastGroupStepId)
            {
                CalculateGroupActionResult(action, _actions, _currentActionIndex);
            }
            //LAS AFTER ACTIONS SE EJECUTAN DESPUES SIEMPRE MENOS EMERGENCY ABORT
            //Esperamos a que todos los rendezvous se resuelvan
            if (!action.IsAfterAction && !action.IsResultEmergencyAbort && action.ActionType != eActionType.ConditionalGoTo)
            {
                //Wait and Dialog
                eResult resultAfterConditions = ExecuteAfterConditions(action);
                //Ejecutar GoToMark
                int newIndex = 0;
                eResult resultExecuteGoToMark = ExecuteGoToMark(action, currentIndex, out newIndex);
                if (resultExecuteGoToMark == eResult.GoToMark)
                {
                    //Nuevo indice en la lista de acciones a ejecutar
                    taskResult.NewIndex = newIndex;
                    taskResult.Result = eResult.GoToMark;
                    return taskResult;
                }
                //Insertar las post acciones
                TaskActionResult resultAfterActions = InsertAfterActions(action, currentIndex);
            }

            return taskResult;

        }

        /// <summary>
        /// Calcular el resultado de un grupo de acciones
        /// TODO:RAUL: Pendiente crear una prueba unitaria
        /// </summary>
        /// <param name="action"></param>
        internal static void CalculateGroupActionResult(PlanActionModel action, ObservableCollection<PlanActionModel> _actions, int _currentActionIndex)
        {
            //Recorremos desde la acción actual(ULTIMA DE GRUPO) hasta la primera acción del grupo
            for (int i = 0; i <= action.LastGroupStepId; i++)
            {
                PlanActionModel groupActionStep = _actions[_currentActionIndex - i];
                //Si alguna acción ha fallado la ultima acción tb falla que es la que se tiene en cuenta para el VALIDATION
                if (groupActionStep.IsGroup && groupActionStep.CurrentResult != eDefinitionResult.OK)
                {
                    //Falla el grupo de acciones.
                    action.CurrentResult = eDefinitionResult.NOK;
                    //Salir del for
                    break;
                }
            }
        }

        /// <summary>
        /// Desplazarse a la nueva etiqueta
        /// </summary>
        /// <param name="currentAction">Acción actual</param>
        /// <param name="currentIndex"> Indice acción actual</param>
        /// <param name="goToMark">Cadena de texto de la marca para saltar a esta marca si gotoMode=Mark</param>
        /// <param name="gotoStep">Cadena de texto de la marca para saltar a esta marca si gotoMode=Step</param>
        /// <param name="newIndex">Indice de la nueva acción</param>
        public static eResult GoToAction(PlanActionModel currentAction, int currentIndex, string goToMark, ObservableCollection<PlanActionModel> actions, out int newIndex, eGotoMode gotoMode, int gotoStep)
        {
            int index = -1;
            //Contamos el reintento
            currentAction.GoToMarkRetriesCount++;

            switch (gotoMode)
            {
                case eGotoMode.Start:
                    index = 0;
                    break;
                case eGotoMode.Finish:
                    index = actions.Count - 1;
                    break;
                case eGotoMode.Step:
                    index = gotoStep - 1;
                    break;
                case eGotoMode.Mark:
                    //No es posible buscar la etiqueta
                    if (goToMark == string.Empty)
                    {
                        newIndex = -1;
                        return eResult.Success;
                    }
                    // Buscar la accion con la marca, obtenemos el indice
                    foreach (var action in actions)
                    {
                        if (action.ComeToMark == goToMark)
                        {
                            index = actions.IndexOf(action);
                        }
                    }
                    ;
                    break;
                case eGotoMode.Undefined:
                    break;
                default:
                    break;
            }

            // Ultimo reintento
            if (currentAction.MaxRetries != 0 && currentAction.GoToMarkRetriesCount >= currentAction.MaxRetries)
            {
                newIndex = -1;
                return eResult.Success;
            }
            else if (currentAction.MaxRetries == 0 && currentAction.GoToMarkRetriesCount > 1)
            {
                //Al menos ejecutarlo una vez
                newIndex = -1;
                return eResult.Success;
            }

            //Si hay marca en otra acción 
            if (index != -1)
            {
                newIndex = index;
                //Desplazarnos a la acción de destino 
                GoToIndex(newIndex, currentIndex, eDefinitionResult.ABORTED_BY_GO_TO, actions);

                //Saltar a la marca
                return eResult.GoToMark;
            }
            else
            {
                //No se encuentra la etiqueta de destino continuamos el flujo.
                newIndex = -1;
                return eResult.Success;
            }
        }

        /// <summary>
        /// Recorre la lista de acciones para saltar a la marca, y actualiza la lista 
        /// Puede ser por una accion GoTo o por una accion GoToMark after conditions.
        /// </summary>
        /// <param name="currentIndex">Indice posición actual de acciones (step)</param>        
        /// <param name="newIndex">Indice nueva posición actual de acciones (step)</param>
        /// <param name="gotoBackResult"> Estado para las acciones intermedias de saltos hacia atrás.</param>
        public static void GoToIndex(int newIndex, int currentIndex, eDefinitionResult gotoBackResult, ObservableCollection<PlanActionModel> actions)
        {
            //Salto hacia adelante
            if (newIndex > currentIndex)
            {
                for (int i = currentIndex + 1; i < newIndex; i++)
                {
                    //Cambiamos el resulado de las acciones intermedias
                    actions[i].ByPassAction();
                }
            }
            //Salto hacia atrás
            if (newIndex < currentIndex)
            {
                //Quitar las afteractions entre los indices
                List<int> indicesToRemove = new List<int>();
                for (int i = newIndex + 1; i < currentIndex; i++)
                {
                    if (actions[i].IsAfterAction)
                    {
                        indicesToRemove.Add(i);
                    }
                    else
                    {
                        //Cambiamos el resultado de las acciones intermedias que no son after actions
                        actions[i].CurrentResult = gotoBackResult;
                        actions[i].ResetStatus();
                    }
                }

                // Sort indices in descending order to avoid shifting issues
                indicesToRemove.Sort((a, b) => b.CompareTo(a));

                foreach (int removeIndex in indicesToRemove)
                {
                    if (removeIndex >= 0 && removeIndex < actions.Count)
                    {
                        actions.RemoveAt(removeIndex);
                    }
                }
            }
        }

        /// <summary>
        /// Ejecuta la acción de tipo GoToMark
        /// </summary>
        /// <param name="currentAction">Acción actual</param>
        /// <param name="currentIndex">Indice actual</param>
        /// <param name="newIndex">Nuevo indice</param>
        /// <returns>Resultado</returns>
        private eResult ExecuteGoToMark(PlanActionModel currentAction, int currentIndex, out int newIndex)
        {
            int index = -1;

            newIndex = index;
            if (currentAction.IsAfterAction) return eResult.Success;

            AfterConditionsJson? afterConditions = null;
            if (currentAction.IsResultSuccess)
            {
                afterConditions = currentAction.AfterOKConditions;
            }
            if (currentAction.IsResultFailed)
            {
                afterConditions = currentAction.AfterNOKConditions;
            }

            if (afterConditions == null) return eResult.Success;

            //Goto después de condiciones
            if (afterConditions.GoToMark.Length > 0)
            {
                // Ultimo reintento
                if (currentAction.GoToMarkRetriesCount > afterConditions.GoToRetries)
                {
                    newIndex = -1;
                    return eResult.Success;
                }
                // Buscar la accion con la marca, obtenemos el indice

                foreach (var action in _actions)
                {
                    if (action.ComeToMark == afterConditions.GoToMark)
                    {
                        index = _actions.IndexOf(action);
                    }
                }
                ;

                //Si hay marca en otra acción 
                if (index != -1)
                {
                    newIndex = index;
                    eDefinitionResult res = currentAction.IsResultSuccess ? eDefinitionResult.CONDITION_OK_GOING_TO : eDefinitionResult.CONDITION_NOK_GOING_TO;
                    GoToIndex(newIndex, currentIndex, res, _actions);

                    //Contamos el reintento
                    currentAction.GoToMarkRetriesCount++;
                    //Saltar a la marca
                    return eResult.GoToMark;
                }
            }

            newIndex = index;
            return eResult.Success;
        }

        /// <summary>
        /// Ejecuta la acción after conditions
        /// </summary>
        /// <param name="currentAction">Acción actual</param>
        private eResult ExecuteAfterConditions(PlanActionModel currentAction)
        {
            if (currentAction.IsAfterAction) return eResult.Success;

            AfterConditionsJson? afterConditions = null;
            if (currentAction.IsResultSuccess)
            {
                afterConditions = currentAction.AfterOKConditions;
            }
            if (currentAction.IsResultFailed)
            {
                afterConditions = currentAction.AfterNOKConditions;
            }
            //No hay after conditions
            if (afterConditions == null) return eResult.Success;


            //TODO: Implementar ShowDialogMessage pasarselo a David.
            //Primero mostrar el dialogo y después comprobar el goto 
            if (afterConditions.ShowDialogMessage.Trim().Length > 0)
            {
                //var dispatcher = System.Windows.Application.Current?.Dispatcher;

                //Mostrar el mensaje
                AfterActionShowDialog message = new AfterActionShowDialog(new PlanActionView(), afterConditions.ShowDialogMessage);
                //Lanzar el evento
                //message.OpenPromptUser += OpenPromptUser;

                //TODO:RAUL: Llamar al promptdispatcher desde la acción AfterActionShowDialog

                //Lanzar tarea de espera y no bloquear el hilo actual para que se propague el evento
                Task<eResult> t = Task<eResult>.Run(() => { return message.Start(); }, _cts.Token);
                //Esperar el ok del usuario utilizando Continue()
                t.Wait();
            }

            if (afterConditions.WaitTime > 0)
            {
                Task.Delay(afterConditions.WaitTime).Wait();
            }

            return eResult.Success;
        }
        /// <summary>
        /// Evento al iniciar
        /// </summary>
        /// <param name="e"></param>
        protected virtual void OnThreadStarted(EventArgs e)
        {
            ThreadStarted?.Invoke(this, e);
        }
        /// <summary>
        /// Una acción de abortar detiene el hilo
        /// </summary>
        /// <param name="e"></param>
        protected virtual void OnThreadAbortAction(bool abortAllStations)
        {
            ThreadAbortActionEvent?.Invoke(this, abortAllStations);
        }
        /// <summary>
        /// Evento al completarse
        /// </summary>
        /// <param name="e"></param>
        protected virtual void OnThreadCompleted(EventArgs e)
        {
            ThreadCompleted?.Invoke(this, e);
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        protected virtual void OnRendezVousEvent(PlanActionModel action)
        {
            RendezVousEvent?.Invoke(this, action);
        }

        /// <summary>
        /// Continues the rendezvous by setting the manual reset 
        /// </summary>
        public void ContinueRendezVous()
        {
            _mre.Set();
        }
        /// <summary>
        /// Calcular el estado en función de las acciones
        /// </summary>        
        public eDefinitionResult CalculateResult()
        {
            return CalculateResult(_actions);
        }
        /// <summary>
        /// Calcular el estado en función de las acciones
        /// </summary>  
        public static eDefinitionResult CalculateResult(ObservableCollection<PlanActionModel> actions)
        {
            bool allTrue = actions.All(x => x.CalculatedResult == eDefinitionResult.OK);
            if (allTrue) return eDefinitionResult.OK;
            return eDefinitionResult.NOK;
        }


    }
}
