using Autis.PLC.Beckoff;
using Autis.PLC.Beckoff.Monitor;
using Serilog;
using System.Collections.Concurrent;
using TestPlan.Entities;
using TestPlan.Logic.OPCUa;
using TestPlan.Logic.Services.Alarms;

namespace TestPlan.Logic.Services
{
    public class AlarmsPLCBeckhoffService : AlarmsService
    {
        #region Properties
        private readonly IOpcUaMonitorService _opc;
        private readonly string[] _dbIds;
        private readonly List<ConfigDbAlarms> _configAlarms;
        public Dictionary<string, CommonDb900X> Instances { get; set; } = new();
        private readonly Dictionary<int, bool[]> _dbAlarmBools = new();
        private bool _slotIndependentSignals;
        /// <summary>
        /// Identificador para el enrutamiendto despues de cambiar una propiedad de alguna suscripcion
        /// </summary>
        private readonly Guid _ownerId = Guid.NewGuid();
        #endregion

        #region Constructor
        public AlarmsPLCBeckhoffService(
                IOpcUaMonitorService opc,
                List<ConfigDbAlarms> configAlarms,
                ConcurrentDictionary<string, string> topicsData)
                : base(topicsData)
        {
            _opc = opc;
            _dbIds = configAlarms.Select(c => c.DB_Number.ToString()).ToArray();
            _configAlarms = configAlarms;
        }
        #endregion

        #region Creacion de la suscripcion a las alarmas del Beckoff PLC
        /// <summary>
        /// Modelo de los Dbs de alarmas del PLC Beckhoff.
        /// </summary>
        public class CommonDb900X
        {
            public bool[] AL { get; set; } // Valor por defecto, puede sobrescribirse según ConfigDbAlarms
        }
        /// <summary>
        /// Función que inicializa las suscripciones a las alarmas del PLC Beckhoff.
        /// </summary>
        /// <returns></returns>
        protected override Dictionary<int, bool[]> InitializeAlarmRawData()
        {
            var configs = new List<MonitoredVariableConfig>();

            foreach (var dbId in _dbIds)
            {
                var model = new CommonDb900X();
                Instances[dbId] = model;

                if (!int.TryParse(dbId, out int dbNumber))
                    continue;

                // Usa el tamaño configurado por base de datos (DB_Size ya es tamaño real del array de bools)
                var alarmConfig = _configAlarms.FirstOrDefault(c => c.DB_Number == dbNumber);
                if (alarmConfig != null)
                    _dbAlarmBools[dbNumber] = new bool[alarmConfig.DB_Size];
                
                // Extraer el sufijo del DBID (último carácter)
                string suffix = dbId.Length > 0 ? dbId[^1].ToString() : "0"; // fallback a "0" si vacío

                configs.Add(new MonitoredVariableConfig
                {
                    Name = $"DB{dbId}_ALARMSST{suffix}.AL",
                    Address = $"DB{dbId}_ALARMSST{suffix}.AL"
                });
            }

            _opc.ValueChanged += OnValueChanged;
            _opc.StartMonitoring(configs);
            Log.Information($"✅ Suscripciones de alarmas inicializadas para DBs: {string.Join(", ", _dbIds)}");

            return _dbAlarmBools;
        }
        #endregion

        #region Lectura del cambio de los arrays de alarmas
        private void OnValueChanged(string variablePath, object value, Guid ownerId)
        {
            if (ownerId != _ownerId) return;

            try
            {
                string dbId = variablePath.Split('_')[0].Replace("DB", "");
                if (!int.TryParse(dbId, out int dbNumber)) return;

                if (value is bool[] boolArray)
                {
                    if (_dbAlarmBools.ContainsKey(dbNumber))
                        _dbAlarmBools[dbNumber] = boolArray;

                    if (Instances.TryGetValue(dbId, out var model))
                        model.AL = boolArray;

                    Log.Information("✅ Actualizadas alarmas para DB{Db}", dbNumber);
                }
                else
                {
                    Log.Warning("⚠️ Formato inesperado en AL para DB{Db}: {Type}", dbNumber, value?.GetType().Name);
                }
            }
            catch (Exception ex)
            {
                Log.Error("❌ Error actualizando alarmas: {Message}", ex.Message);
            }
        }
        #endregion

        #region Métodos de lectura de señales de emergencia
        /// <summary>
        /// Esta funcion lo que hace es acceder a la lectura coninua que estamos haciendo de nuestras alarmas y devulve una copia al 
        /// servicio padre para que pueda evaluar las alarmas. 
        /// </summary>
        /// <param name="configAlarms"></param>
        /// <returns></returns>
        internal override Dictionary<int, bool[]> ReadRawData(List<ConfigDbAlarms> configAlarms)
        {
            var result = new Dictionary<int, bool[]>();

            foreach (var config in configAlarms)
            {
                if (_dbAlarmBools.TryGetValue(config.DB_Number, out var data))
                {
                    result[config.DB_Number] = (bool[])data.Clone(); // Clonamos para evitar referencias
                }
                else
                {
                    result[config.DB_Number] = new bool[config.DB_Size]; // fallback vacío si no existe aún
                }
            }

            return result;
        }

        /// <summary>
        /// Lee la señal de emergencia del modelo.
        /// </summary>
        /// <param name="configDbAlarmsEmergency">Configuración necesaria para leer la señal</param>
        /// <returns>Valor de la señal</returns>
        protected override bool ReadEmergencySignal(ConfigDbAlarmsEmergency configDbAlarmsEmergency)
        {
            var value = OPCUaPLCContext.DataManager.GetValueByPath(configDbAlarmsEmergency.ModelPath, configDbAlarmsEmergency.SlotId);
            return value is bool b && b;
        }
        #endregion
    }
}
