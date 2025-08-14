using System.Collections.Concurrent;
using Autis.PLC.S7;
using TestPlan.Entities;
using TestPlan.Logic.Models;
using TestPlan.Logic.Services.Alarms;

namespace TestPlan.Logic.Services
{
    /// <summary>
    /// Simulación de alarma para parar el ciclo en cualquier momento
    /// </summary>
    public class AlarmsPLCSiemensService :  AlarmsService
    {
        #region Properties
        /// <summary>
        /// Objeto PLC para la lectura de alarmas
        /// </summary>
        protected readonly PlcDb _plc;

        #endregion

        #region Constructor

        /// <summary>
        /// Constructor
        /// </summary>
        public AlarmsPLCSiemensService(ConcurrentDictionary<string, string> topicsData):base(topicsData)
        {
            // Obtiene los datos del PLC
            string ip = Config.Instance.General.ConfiguracionPLC.IP;
            short rack = (short)Config.Instance.General.ConfiguracionPLC.Rack;
            short slot = (short)Config.Instance.General.ConfiguracionPLC.Slot;

            // Crea el objeto PLC
            _plc = new PlcDb(ip, rack, slot);

            _readingTask = Task.CompletedTask; // Inicializa la tarea para evitar errores de referencia nula
        }

        #endregion

        #region Alarm Methods
        /// <summary>
        /// Lee los datos de las alarmas del PLC
        /// </summary>
        /// <param name="configAlarms">DBs del PLC que hay que leer</param>
        /// <returns>Datos de cada DB del PLC como array de booleanos</returns>
        internal override Dictionary<int, bool[]> ReadRawData(List<ConfigDbAlarms> configAlarms)
        {
            Dictionary<int, bool[]> readedData = new Dictionary<int, bool[]>();

            // Para cada DB del PLC, lee las alarmas
            foreach (var configAlarm in configAlarms)
            {
                // Lee los bytes del PLC
                byte[] alarmData = _plc.ReadBytes(configAlarm.DB_Number, configAlarm.Offset_Ini, configAlarm.Data);
                // Convierte los bytes a un array de booleanos
                bool[] alarmBits = BytesToBoolArray(alarmData);
                readedData[configAlarm.DB_Number] = alarmBits;
            }

            return readedData;
        }

        /// <summary>
        /// Lee la señal de emergencia del PLC
        /// </summary>
        /// <param name="configDbAlarmsEmergency">Configuración necesaria para leer la señal</param>
        /// <returns>Valor de la señal</returns>
        protected override bool ReadEmergencySignal(ConfigDbAlarmsEmergency configDbAlarmsEmergency)
        {
            return _plc.ReadBit(configDbAlarmsEmergency.DB_Number, configDbAlarmsEmergency.Address, configDbAlarmsEmergency.BitNumber);
        }

        #endregion
    }
}