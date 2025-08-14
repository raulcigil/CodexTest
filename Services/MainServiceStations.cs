using Autis.PLC.Enums;
using Dapper.FluentMap;
using Microsoft.Extensions.Hosting;
using Org.BouncyCastle.Bcpg.Sig;
using Serilog;
using System.Collections.Concurrent;
using TestPlan.DataAccess.Base;
using TestPlan.Entities;
using TestPlan.Entities.Enumeraciones;
using TestPlan.Entities.Map;
using TestPlan.Entities.Views;
using TestPlan.Logic.Configuration;
using TestPlan.Logic.ContinuosReading.Eventos.Aggregator;
using TestPlan.Logic.ContinuousReading.Communication.PoolConnections;
using TestPlan.Logic.Interfaces;
using TestPlan.Logic.Models;
using TestPlan.Logic.OPCUa;
using TestPlan.Logic.Services.Alarms;
using TestPlan.Logic.Services.Mqtt;
using static Autis.PLC.Beckoff.PlcOpcUaConnector;

namespace TestPlan.Logic.Services
{
    /// <summary>
    /// Clase principal de gestión del orquestador
    /// </summary>
    public class MainServiceStations : BackgroundService
    {
        #region Properties
        /// <summary>
        /// Cancellation 
        /// </summary>
        CancellationTokenSource _cts = new CancellationTokenSource();
        /// <summary>
        /// Ciclo iniciado
        /// </summary>
        AlarmsService _alarms = null!;
        /// <summary>
        /// Ciclo iniciado
        /// </summary>
        /// 
        //CheckerService _checker = null!;
        public CheckerService Checker { get; private set; } = null!;
        /// <summary>
        /// Servicio de ping
        /// </summary>
        NetworkDiagramService _diagramService = null!;
        /// <summary>
        /// Servicio MQTT
        /// </summary>
        MqttPublisherService _mqttPublisher = null!;
        /// <summary>
        /// Lista de estaciones 
        /// </summary>
        /// TODO: Valorar usar una ConcurrentBag (puede no ser necesario) https://learn.microsoft.com/es-es/dotnet/api/system.collections.concurrent.concurrentbag-1?view=net-8.0
        StationService[] stationServices = null;
        /// <summary>
        /// Número de estaciones
        /// </summary>
        private int _numStations = 1;
        /// <summary>
        /// Evento que lanza un prompt al usuario
        /// </summary>
        public event EventHandler<PromptUserData> OpenPromptUserEvent = delegate { };
        /// <summary>
        /// 
        /// </summary>
        private static bool _isMapperInitialized = false;

        /// <summary>
        /// Contructor con la inicialización 
        /// </summary>
        public MainServiceStations()
        {
            //Inicialización de la cadena de conexión a la base de datos
            ConfigurationReader.LoadConnectionString();

            if (!_isMapperInitialized)
            {
                FluentMapper.Initialize(config =>
                {
                    config.AddMap(new ConfigGeneralMap());
                    config.AddMap(new ConfigMeasurementChannelsMap());
                    config.AddMap(new ConfigSlotsMap());
                    config.AddMap(new ConfigStationsMap());
                    config.AddMap(new ConfigurationAlarmsMap());
                    config.AddMap(new HistoryDetailActionMap());
                    config.AddMap(new HistoryDetailSubactionMap());
                    config.AddMap(new NetworkDiagramMap());
                });

                _isMapperInitialized = true;
            }

            //Obtener la configuración general
            using (ConfigGeneralLogic configLo = new ConfigGeneralLogic())

            //Obtener la configuración de las alarmas
            using (ConfigDbAlarmsLogic configDbAlarmsLo = new ConfigDbAlarmsLogic())

            using (DefinitionCyclemodeLogic logic = new DefinitionCyclemodeLogic())
            {
                //Configuración general
                GetConfigGeneral? currentConfig = configLo.GetCurrentConfiguration(1);

                if (currentConfig == null)
                {
                    Log.Error("No se ha podido cargar la configuración actual.");
                    throw new Exception("No se ha podido cargar la configuración actual.");
                    //ErrA00026
                }

                Config.Instance.UpdateConfig(currentConfig);

                CycleModeView cycleModeView = logic.GetCyclemodesData();
                if (currentConfig == null)
                {
                    Log.Error("No se ha podido cargar el CycleMode.");
                    throw new Exception("No se ha podido cargar el CycleMode.");
                    //ErrA00027
                }
                //Actualizar el modo del ciclo
                Config.Instance.UpdateCycleModeData(cycleModeView);

                //Configuración de las alarmas
                Config.Instance.DbAlarms = configDbAlarmsLo.GetConfigDbAlarms();
            }

            // Comprueba si hay algún error en la configuración leida
            if (!CheckStatus())
            {
                Log.Error("Error loading configuration.");
                throw new Exception("Error loading configuration.");
            }
        }

        /// <summary>
        /// Realiza las comprobaciones necearias para verificar el estado del sistema antes de lanzar un ciclo
        /// </summary>
        /// <returns>true si el estado es correcto</returns>
        private bool CheckStatus()
        {
            //Por ahora solo estaciones de este tipo
            if (Config.Instance.General.Station.StationType != eStationType.Single)
            {
                Log.Information($"Exiting because the station type is not supported ({Config.Instance.General.Station.StationType.ToString()}).");
                //ErrA00028
                return false;
            }

            // Verificar la configuración del PLC
            string ip = Config.Instance.General.ConfiguracionPLC.IP;
            int? rack = Config.Instance.General.ConfiguracionPLC.Rack;
            int? slot = Config.Instance.General.ConfiguracionPLC.Slot;
            if (string.IsNullOrEmpty(ip) || rack == null || slot == null)
            {
                Log.Information("PLC Configuration -> IP: {Ip}, Rack: {Rack}, Slot: {Slot}", ip, rack, slot);
                return false;
            }

            return true;
        }


        #endregion

        #region Function Start / ExecuteAsync
        /// <summary>
        /// Inicia el orquestador y lanzar los servicios necesarios, triggers, alarms, etc.
        /// </summary>
        /// 


        public async void Start()
        {
            //Cambiando la cultura
            // Set the culture to US English
            //CultureInfo usCulture = new CultureInfo("en-US");
            //Thread.CurrentThread.CurrentCulture = usCulture;
            //Thread.CurrentThread.CurrentUICulture = usCulture;

            //Cargar los devices ciclicos
            using ConfigCyclicReaderDeviceLogic configCyclicReaderDeviceLogic = new ConfigCyclicReaderDeviceLogic();
            List<CyclicReaderDeviceConfig> cdevices = configCyclicReaderDeviceLogic.GetAll();
            AppConfig.CyclicDevicesConfig = cdevices;

            //AppConfig.CyclicDevicesConfig = _config.GetSection("Devices").Get<List<CyclicReaderDeviceConfig>>() ?? new List<CyclicReaderDeviceConfig>();
            // Diccionario donde se guardan los datos a pulicar en MQTT
            // Tiene la estructura: [nombre de topic (sin concatenar)] -> [string de datos a publicar]
            ConcurrentDictionary<string, string> topicsData = new ConcurrentDictionary<string, string>();

            //Nuevo token de cancelación 
            _cts = new CancellationTokenSource();
            //-----------------------------CHECKER-----------------------------
            //Servicio para comprobar y obtener datos actualizados de la bd
            Checker = new CheckerService();
            //Checker.Start(); //Deshabilito el inicio de este comprobador.

            //---------DEBUG
            //Task.Delay(1000).Wait();
            //_checker.Stop();
            //_checker.Start();
            
            //-----------------------------NETWORK DIAGRAM PING DEVICES -----------------------------
            //Servicio para comprobar el ping a los diferentes dispositivos y publicar el estado en MQTT
            _diagramService = new NetworkDiagramService(topicsData);
            _diagramService.Start();
            //-----------------------------MQTT PUBLISHERS -----------------------------
            // Servicio para publicar en MQTT
            // Cramos el agregator para recibir eventos externos por MQTT 
            var eventAggregator = SimpleEventAggregator.Instance;

            //Conexión al MQTT Broker por TCP y no por WS (WS solo para web)
            MqttConfigModel mqttConfig = new MqttConfigModel
            {
                BrokerAddress = Config.Instance.General.MqttWsConfig.IP,
                BrokerPort = Config.Instance.General.MqttWsConfig.Port,
                ClientId = "Core"
            };

            // 1. Inicia subscripción MQTT que publica al eventAggregator
            await MqttSubscriber.InitAsync(eventAggregator, AppConfig.Mqtt.SuscribeTopic);
            await MqttPublisherService.InitAsync(AppConfig.Mqtt.Broker, AppConfig.Mqtt.PublishPort);

            //// TODO: Sergio: Pasar el id de la estacion correcto
            ///
            _mqttPublisher = new MqttPublisherService(mqttConfig, topicsData, stationId: 1);   
            _mqttPublisher.Start();

            //Abrimos un Delegado para hacer uso del pool sde sesiones OPCUA
            OpcUaSessionBridge.GetSessionFunc = async endpoint =>
            {
                return await OpcUaSessionPool.Instance.GetOrCreateSessionAsync(endpoint);
            };

            // Carga la configuración de los slots
            using var slotLogic = new ConfigSlotsLogic();
            var slots = slotLogic.GetAll();


            //-----------------------------Inicializacion PLC y Servicio de Alarmas -----------------------------
            // Inicializar el contexto OPC UA para el PLC
            var plcConfig = Config.Instance.General.ConfiguracionPLC;
            string endPoint = $"opc.tcp://{plcConfig.IP}:4840";
            // ✅ Inicializamos todo el contexto OPC UA (incluyendo alarmas correctas) esto contendra tanto alarmas DB7 y OPCUA
            var plcType = AppConfig.PLC.Type;

            // Inicializacion del PLC de beckoff y el ALarm service asociado
            //Carga de alarmas que se leerán del PLC
            if (plcType == ePlcType.Beckhoff)
            {
                _alarms = await OPCUaPLCContext.InitializeAsync(endPoint, slots.Count, topicsData);
            }
            else if (plcType == ePlcType.S7)
            {
                _alarms = new AlarmsPLCSiemensService(topicsData);
            }

            // Obtiene la señales de parada de emergencia e iniciar el servicio de alarmas
            using (ConfigDbAlarmsEmergencyLogic configDbAlarmsEmergencyLo = new ConfigDbAlarmsEmergencyLogic())
            {
                ConfigDbAlarmsEmergency[] monitoredEmergencySignals = configDbAlarmsEmergencyLo.GetAll().ToArray();
                _alarms.Start(monitoredEmergencySignals);
                _alarms.AlarmAlert += Alarm_Alert;
            }

            //-----------------------------STATIONS A LA VEZ-----------------------------
            //Servicio para gestionar las estaciones, iniciamos tantos hilos como estaciones tenemos en Config_Stations
            using (ConfigStationsLogic confStation = new ConfigStationsLogic())
            {
                _numStations = confStation.GetAll().Count;
                stationServices = new StationService[_numStations];
            }

            for (int i = 0; i < stationServices.Length; i++)
            {
                int stationId = i + 1;

                var station = new StationService(stationId);

                stationServices[i] = station;

                // Launch using Task-based internal method (no need for Thread)
                station.StartStation();
            }
        }


        /// <summary>
        /// Ejecuta la tarea como backgroundservice en un servicio de Windows
        /// </summary>
        /// <param name="stoppingToken"></param>
        /// <returns></returns>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Start();

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(100, stoppingToken);
            }
        }

        #endregion

        #region Stop Function
        /// <summary>
        /// Liberando recursos y parando el orquestador, log info
        /// </summary>
        public void Stop()
        {

        }
        #endregion

        #region Alarms
        /// <summary>
        /// Alarma
        /// </summary>
        public void Alarm_Alert(object? sender, AlarmDataModel data)
        {
            Parallel.ForEach(stationServices, station => station.AlarmAlert());
        }
        #endregion

        #region User Interaction Fuctions
        /// <summary>
        /// Lanza el evento OnStarted
        /// </summary>
        /// <param name="e"></param>
        protected virtual void OnOpenPromptUser(PromptUserData e)
        {
            OpenPromptUserEvent?.Invoke(this, e);
        }

        /// <summary>
        /// Fin de ciclo por trigger
        /// </summary>
        public void OpenPromptUser(object? sender, PromptUserData data)
        {
            //Propagar el evento hasta la app WPF
            OpenPromptUserEvent?.Invoke(sender, data);
        }
        #endregion

    }
}