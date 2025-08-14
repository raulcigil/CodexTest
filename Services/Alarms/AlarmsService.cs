using System.Collections.Concurrent;
using System.Text.Json;
using Serilog;
using TestPlan.Entities;
using TestPlan.Logic.Models;
using TestPlan.Logic.Services.Cycle;

namespace TestPlan.Logic.Services.Alarms
{
    /// <summary>
    /// Simulación de alarma para parar el ciclo en cualquier momento
    /// </summary>
    public abstract class AlarmsService
    {
        #region Fields
        /// <summary>
        /// Token para cancelar esta tarea
        /// </summary>
        protected CancellationTokenSource _cts = new CancellationTokenSource();
        /// <summary>
        /// Tarea para la escucha del evento
        /// </summary>
        protected Task? _readingTask;
        /// <summary>
        /// Tiempo en ms para comprobar la lectura del evento
        /// </summary>
        protected const int C_PollingInterval = 100;
        /// <summary>
        /// // Evento para notificar el inicio del ciclo (Parte clave del Observer)
        /// </summary>
        public event EventHandler<AlarmDataModel>? AlarmAlert;

        /// <summary>
        /// Señal de parada de emergencia
        /// </summary>
        protected bool _emergencyStop = false;

        /// <summary>
        /// Ciclo automático
        /// </summary>
        protected bool _autoCycle = false;

        /// <summary>
        /// Habilitada la notificación de alarmas
        /// </summary>
        protected bool _enabled = true;


        /// <summary>
        /// Alarmas activas (código de alarma, datos de alarma)
        /// </summary>
        protected readonly Dictionary<int, AlarmDataModel> _activeAlarms = new Dictionary<int, AlarmDataModel>();

        /// <summary>
        /// Datos leídos de las alarmas del PLC (DB, array de booleanos)
        /// </summary>
        protected Dictionary<int, bool[]> _alarmsRawData = new Dictionary<int, bool[]>();

        /// <summary>
        /// Topics Data
        /// </summary>
        protected readonly ConcurrentDictionary<string, string> _topicsData;

        private bool _slotIndependentSignals;

        /// <summary>
        /// Topic para MQTT
        /// </summary>
        public const string C_TopicName = "ActiveAlarms";

        #endregion

        #region Static 

        #endregion

        #region Properties

        public bool EmergencyStop { get => _emergencyStop; }

        public bool AutoCycle { get => _autoCycle; }

        #endregion

        #region Constructor

        /// <summary>
        /// Constructor
        /// </summary>
        public AlarmsService(ConcurrentDictionary<string, string> topicsData)
        {
            _topicsData = topicsData;
            _readingTask = Task.CompletedTask; // Inicializa la tarea para evitar errores de referencia nula
        }

        #endregion

        #region Enable / Disable

        /// <summary>
        /// Habilitar la alarma (solo se lanzan los eventos si está habilitada)
        /// </summary>
        public void Enable()
        {
            _enabled = true;
        }

        /// <summary>
        /// Deshabilitar la alarma (solo se lanzan los eventos si está habilitada)
        /// </summary>
        public void Disable()
        {
            _enabled = false;
        }

        #endregion

        #region Abstract methods

        internal abstract Dictionary<int, bool[]> ReadRawData(List<ConfigDbAlarms> configAlarms);

        #endregion

        #region Alarm Methods
        /// <summary>
        /// Iniciar la escucha de los eventos
        /// </summary>
        public virtual void Start(ConfigDbAlarmsEmergency[] monitoredEmergencySignals)
        {
            Log.Information("StartListening AlarmService");

            // Comprueba si el cancelador ya ha sido cancelado
            if (_cts.IsCancellationRequested)
            {
                _cts = new CancellationTokenSource();
            }

            _alarmsRawData = InitializeAlarmRawData(); // ← Este será diferente por cada tipo de PLC

            _readingTask = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    // Lee la señal de parada de emergencia y el ciclo automático
                    ReadEmergencySignals(true, monitoredEmergencySignals);
                    if (_enabled)
                    {
                        try
                        {
                            // obtiene la lista de alarmas a leer
                            List<ConfigDbAlarms> configDbAlarmsToRead = RunPolling(Config.Instance.DbAlarms);

                            // Comprueba si hay alarmas a leer
                            if (configDbAlarmsToRead.Count > 0)
                            {
                                // Lee las alarmas del PLC
                                Dictionary<int, bool[]> readedData = ReadRawData(configDbAlarmsToRead);
                                EvaluateAlarms(configDbAlarmsToRead, readedData);
                            }
                        }
                        catch (Exception ex)
                        {
                            // Manejo de errores
                            Log.Error(ex, "");
                        }
                    }
                    await Task.Delay(C_PollingInterval, _cts.Token);
                }
            }, _cts.Token);
        }
        /// <summary>
        /// Inicializa los datos crudos de alarmas (solo aplica a ciertos PLCs).
        /// Beckhoff lo puede ignorar.
        /// </summary>
        protected virtual Dictionary<int, bool[]> InitializeAlarmRawData()
        {
            _alarmsRawData.Clear();

            // Por defecto, implementación S7
            foreach (var alarmConfig in Config.Instance.DbAlarms)
            {
                _alarmsRawData.Add(alarmConfig.DB_Number, new bool[alarmConfig.DB_Size * 8]);
            }

            return _alarmsRawData;
        }
        /// <summary>
        /// Detiene la escucha
        /// </summary>
        public void Stop()
        {
            Log.Debug("StopListening AlarmService");
            _cts.Cancel();
            _readingTask?.Wait(TimeSpan.FromSeconds(5));
            _cts.Dispose();
        }
        /// <summary>
        /// Comprueba las alarmas que hay que leer
        /// </summary>
        /// <param name="configDbAlarms">Lista de configuraciones de alarmas</param>
        /// <returns>Lista de alarmas a leer</returns>
        internal static List<ConfigDbAlarms> RunPolling(List<ConfigDbAlarms> configDbAlarms)
        {
            List<ConfigDbAlarms> configDbAlarmsToRead = new List<ConfigDbAlarms>();
            DateTime currentTime = DateTime.Now;

            // Comprueba si ha pasado el tiempo de polling para cada alarma
            foreach (var alarm in configDbAlarms)
            {
                // Comprueba si la alarma no se ha leído nunca o si ha pasado el tiempo de polling para volver a leerla
                if (alarm.Last_Read == null || currentTime.Subtract(alarm.Last_Read.Value).TotalSeconds >= alarm.Polling)
                {
                    // Añade la alarma a la lista de alarmas a leer
                    configDbAlarmsToRead.Add(alarm);
                    // Actualiza la última lectura
                    alarm.Last_Read = currentTime;
                }
            }
            return configDbAlarmsToRead;
        }
        /// <summary>
        /// Evalúa las alarmas leídas del PLC para detectar cambios.
        /// </summary>
        /// <param name="configAlarms">DBs que hay que comprobar.</param>
        /// <param name="readedData">Datos leídos la última vez.</param>
        protected void EvaluateAlarms(List<ConfigDbAlarms> configAlarms, Dictionary<int, bool[]> readedData)
        {
            // Lista para almacenar las alarmas que han cambiado
            var changedAlarms = new List<AlarmDataModel>();

            // Solo se crea el objeto ConfigurationAlarmsLogic si es necesario
            ConfigurationAlarmsLogic? configurationAlarmsLogic = null;

            try
            {
                foreach (var dbNumber in configAlarms.Select(configAlarm => configAlarm.DB_Number))
                {
                    bool[] currentAlarms = readedData[dbNumber];
                    bool[] previousAlarms = _alarmsRawData[dbNumber];

                    // Comprueba si han habido cambios desde la última lectura
                    for (int i = 0; i < currentAlarms.Length; i++)
                    {
                        if (currentAlarms[i] == previousAlarms[i])
                        {
                            // No ha cambiado el estado de la alarma
                            continue;
                        }

                        // Obtiene el código de la alarma
                        int code = GetAlarmCode(dbNumber, i, Config.Instance.DbAlarms);

                        // Comprueba si la alarma ya está activa
                        if (!_activeAlarms.TryGetValue(code, out AlarmDataModel? alarmData))
                        {
                            // Nueva alarma detectada
                            // Solo crea el objeto ConfigurationAlarmsLogic si es distinto de null
                            configurationAlarmsLogic ??= new ConfigurationAlarmsLogic();
                            AddNewAlarm(code, currentAlarms[i], configurationAlarmsLogic, changedAlarms);
                        }
                        else
                        {
                            // Cambio de estado en una alarma activa
                            UpdateExistingAlarm(code, currentAlarms[i], alarmData, changedAlarms, _activeAlarms);
                        }
                    }

                    // Actualiza los datos leídos para la próxima evaluación
                    _alarmsRawData[dbNumber] = currentAlarms;
                    UpdateTopicData();
                }
            }
            finally
            {
                // Libera el objeto ConfigurationAlarmsLogic si fue creado
                configurationAlarmsLogic?.Dispose();
            }

            InsertChangedAlarms(changedAlarms);
        }
        #region Auxiliary methods
        /// <summary>
        /// Obtiene el código de la alarma a partir del número de DB y el índice del bit
        /// </summary>
        /// <param name="db">DB del PLC</param>
        /// <param name="bitIndex">Posición del bit</param>
        /// <returns>Código de la alarma</returns>
        internal static int GetAlarmCode(int db, int bitIndex, List<ConfigDbAlarms> configDbAlarms)
        {
            const int C_BitsPerByte = 8;
            int code = 0;

            foreach (var alarmConfig in configDbAlarms)
            {
                if (alarmConfig.DB_Number == db)
                {
                    if (bitIndex >= alarmConfig.DB_Size * C_BitsPerByte)
                    {
                        throw new ArgumentOutOfRangeException($"Bit index {bitIndex} is out of range for DB {db}");
                    }
                    code += bitIndex;
                    return code;
                }
                code += alarmConfig.DB_Size * C_BitsPerByte;
            }

            throw new ArgumentException($"DB {db} not found, unable to obtain alarm code.");
        }

        /// <summary>
        /// Convierte un array de bytes a un array de booleanos que representa
        /// cada bit en la representación en complemento a dos de cada byte.
        /// El elemento 0 del array corresponde al bit menos significativo (LSB) del primer byte.
        /// </summary>
        /// <param name="numbers">Array de bytes a convertir.</param>
        /// <returns>Array de booleanos, donde cada elemento representa un bit de los bytes de entrada.</returns>
        internal static bool[] BytesToBoolArray(byte[] numbers)
        {
            // Constante en lugar de variable local
            const int C_BitsPerByte = 8;

            // Precalcular el tamaño para evitar redimensionamientos
            int length = numbers.Length * C_BitsPerByte;
            bool[] bits = new bool[length];

            // Usar span para mejorar el rendimiento de acceso a memoria
            Span<byte> numbersSpan = numbers;
            Span<bool> bitsSpan = bits;

            for (int i = 0; i < numbers.Length; i++)
            {
                byte number = numbersSpan[i];
                int baseIndex = i * C_BitsPerByte;

                // Desenrollar el bucle interno para reducir comprobaciones de condición
                bitsSpan[baseIndex] = (number & 0x01) != 0;
                bitsSpan[baseIndex + 1] = (number & 0x02) != 0;
                bitsSpan[baseIndex + 2] = (number & 0x04) != 0;
                bitsSpan[baseIndex + 3] = (number & 0x08) != 0;
                bitsSpan[baseIndex + 4] = (number & 0x10) != 0;
                bitsSpan[baseIndex + 5] = (number & 0x20) != 0;
                bitsSpan[baseIndex + 6] = (number & 0x40) != 0;
                bitsSpan[baseIndex + 7] = (number & 0x80) != 0;
            }

            return bits;
        }

        /// <summary>
        /// Intenta añadir una nueva alarma a la lista de activas y de cambios.
        /// </summary>
        /// <param name="code"></param>
        /// <param name="status"></param>
        /// <param name="configurationAlarmsLogic"></param>
        /// <param name="changedAlarms"></param>
        protected void AddNewAlarm(int code, bool status, ConfigurationAlarmsLogic configurationAlarmsLogic, List<AlarmDataModel> changedAlarms)
        {
            // Obtiene la información de la alarma a partir del código
            var alarmConfigView = configurationAlarmsLogic.GetAlarmConfig(code);

            if (alarmConfigView == null)
            {
                // No se encuentra la alarma
                Log.Error("Alarm code {Code} not found", code);
                return;
            }

            var newAlarm = new AlarmDataModel
            {
                Code = code,
                Status = status,
                Time = DateTime.Now,
                Description = alarmConfigView.Description,
                Action = alarmConfigView.Action,
                Group = alarmConfigView.Group,
                Info = alarmConfigView.CustomerComment
            };

            // Añade la nueva alarma a la lista de activas
            _activeAlarms.TryAdd(code, newAlarm);
            // Añade la nueva alarma a la lista de cambios para insertar en la base de datos
            changedAlarms.Add(newAlarm);

            Log.Information("New alarm: Code: {Code}, Group: {Group}, Description: {Description}, Action: {Action}",
                code, alarmConfigView.Group, alarmConfigView.Description, alarmConfigView.Action);
        }

        /// <summary>
        /// Actualiza el estado de una alarma activa y la añade a la lista de cambios.
        /// </summary>
        /// <param name="code"></param>
        /// <param name="status"></param>
        /// <param name="alarmData"></param>
        /// <param name="changedAlarms"></param>
        internal static void UpdateExistingAlarm(int code, bool status, AlarmDataModel alarmData, List<AlarmDataModel> changedAlarms, Dictionary<int, AlarmDataModel> activeAlarms)
        {
            alarmData.Status = status;
            alarmData.Time = DateTime.Now;
            changedAlarms.Add(alarmData);
            if (!status)
            {
                // Si la alarma se desactiva, la elimina de la lista de activas
                activeAlarms.Remove(code);
            }
        }

        /// <summary>
        /// Inserta en la base de datos las alarmas que han cambiado y lanza eventos si procede.
        /// </summary>
        /// <param name="changedAlarms"></param>
        private void InsertChangedAlarms(List<AlarmDataModel> changedAlarms)
        {
            if (changedAlarms.Count == 0)
            {
                // No hay cambios que procesar
                return;
            }

            using var activeAlarmsLogic = new ActiveAlarmsLogic();
            foreach (var alarm in changedAlarms)
            {
                activeAlarmsLogic.InsertNewAlarm(alarm.Code, alarm.Group, alarm.Status ? 1 : 0, alarm.Time, alarm.Info);

                if (alarm.Status)
                {
                    // Si la alarma está habilitada, lanza el evento
                    OnLaunchAlarm(alarm);
                }
            }
        }

        /// <summary>
        /// Actualiza la información de las alarmas activas en el topic
        /// </summary>
        private void UpdateTopicData()
        {
            _topicsData[C_TopicName] = JsonSerializer.Serialize(_activeAlarms.Values.ToList());
        }

        #endregion

        #endregion

        #region Emergency Signals

        /// <summary>
        /// Lee una sola señal de emergencia a partir de la configuración etablecida.
        /// Depende del tipo de PLC y su implementación.
        /// </summary>
        /// <param name="configDbAlarmsEmergency">Datos necesarios para leer la señal</param>
        /// <returns>Valor de la señal</returns>
        protected abstract bool ReadEmergencySignal(ConfigDbAlarmsEmergency configDbAlarmsEmergency);

        /// <summary>
        /// Lectura de las señales de emergencia que nos bloquearan y pararan los ciclos de producción.
        /// </summary>
        /// <param name="unifiedEmergencySignal"></param>
        protected void ReadEmergencySignals(bool unifiedEmergencySignal, ConfigDbAlarmsEmergency[] monitoredEmergencySignals)
        {
            try
            {
                // Diccionario de estados por slot
                var slotSafetyMap = new Dictionary<int, SlotSafetyStatus>();
                var globalSignals = new List<bool>();

                foreach (var kvp in monitoredEmergencySignals)
                {
                    bool signalValue = false;
                    if (kvp.Enabled)
                    {
                        signalValue = ReadEmergencySignal(kvp);
                    }

                    int slotId = kvp.SlotId;
                    if (slotId == 0) // Señales globales (pueden venir varias con slotId 0)
                    {
                        globalSignals.Add(signalValue);
                    }
                    else // Señales individuales por slot
                    {
                        // Comprobación en caso de que haya más de una señal por slot
                        if (slotSafetyMap.TryGetValue(slotId, out SlotSafetyStatus? value))
                        {
                            // Si ya existe, combinamos el estado
                            signalValue = value.HasEmergency || signalValue;
                        }

                        slotSafetyMap[slotId] = new SlotSafetyStatus
                        {
                            SlotId = slotId,
                            HasEmergency = signalValue
                        };
                    }
                }

                // Evaluamos si alguna señal global está activa → parar todos los slots
                bool anyGlobalAlarm = globalSignals.Any(x => x);

                if (anyGlobalAlarm)
                {
                    foreach (var slotId in slotSafetyMap.Keys)
                    {
                        CancelCycleForSlot(slotId);
                    }

                    _slotIndependentSignals = true;
                }
                else
                {
                    // Verificamos señales individuales
                    foreach (var kv in slotSafetyMap)
                    {
                        if (kv.Value.HasEmergency)
                        {
                            CancelCycleForSlot(kv.Key);
                        }
                    }

                    _slotIndependentSignals = slotSafetyMap.Values.Any(v => v.HasEmergency);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "❌ Error reading emergency signals: {Message}", ex.Message);
            }
        }

        protected static void CancelCycleForSlot(int slotId)
        {
            SlotCycleRegistry.CancelSlotCycle(slotId);
        }

        #endregion

        #region Events

        /// <summary>
        /// Lanzar el evento de alarma
        /// </summary>
        protected void OnLaunchAlarm(AlarmDataModel alarmDataModel)
        {
            AlarmAlert?.Invoke(this, alarmDataModel);
        }

        #endregion

        #region Dispose

        /// <summary>
        /// Dispose
        /// </summary>
        public void Dispose() => Stop();

        #endregion
    }
}