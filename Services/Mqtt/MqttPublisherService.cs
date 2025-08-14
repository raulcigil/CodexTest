using Microsoft.IdentityModel.Tokens;
using MQTTnet;
using MQTTnet.Diagnostics.Logger;
using MQTTnet.Implementations;
using Serilog;
using System.Collections.Concurrent;
using System.Text;
using TestPlan.Logic.Models;
using TestPlan.Logic.Services.Alarms;

namespace TestPlan.Logic.Services.Mqtt
{
    public class MqttPublisherService : IDisposable
    {
        #region Fields

        /// <summary>
        /// token de cancelación
        /// </summary>
        private CancellationTokenSource _cts = new CancellationTokenSource();

        /// <summary>
        /// Intervalo de publicación en ms
        /// </summary>
        private TimeSpan _publishInterval = TimeSpan.FromMilliseconds(500);

        private static IMqttClient _client;

        /// <summary>
        /// Cliente MQTT
        /// </summary>
        private readonly MqttHelper _mqttHelper;

        /// <summary>
        /// Diccionario con los datos a publicar
        /// </summary>
        private readonly ConcurrentDictionary<string, string> _topicsData;

        /// <summary>
        /// Diccionario con los nombres de los topics
        /// </summary>
        private readonly Dictionary<string, string> _topicNames;

        public const string C_LifeBitTopicName = "LifeBit";

        #endregion

        #region Constructor

        public MqttPublisherService(MqttConfigModel config, ConcurrentDictionary<string, string> topicsData, int stationId)
        {
            _mqttHelper = new MqttHelper(config);
            _topicsData = topicsData;
            _topicNames = GenerateTopicNames(config.StationName, stationId);
        }

        #region Create conection
        public static async Task InitAsync(string broker, int port, string clientID = "server/data", bool useTls = false)
        {
            var logger = new MqttNetNullLogger();
            var adapterFactory = new MqttClientAdapterFactory();
            _client = new MqttClient(adapterFactory, logger);

            var options = new MqttClientOptionsBuilder()
                .WithTcpServer(broker, port)
                .WithClientId(clientID)
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
                .WithCleanSession()
                .Build();

            var result = await _client.ConnectAsync(options);
            Log.Information($"[MQTT] 🟢 Conectado a broker {broker}:{port}:{clientID}");
        }
        #endregion
        #endregion

        #region Auxiliar

        /// <summary>
        /// Genera los nombres de los topics
        /// </summary>
        /// <param name="stationName">Nombre de la estación</param>
        /// <param name="stationId">ID de la estación</param>
        /// <returns>Diccionario con los nombres de los topics</returns>
        [Obsolete("Posiblemente no haga falta generar los topicnames en la colección, cada servicio que publique donde quiera en la instancia de _topicsData(singleton)")]
        private static Dictionary<string, string> GenerateTopicNames(string stationName, int stationId)
        {
            // Genera el nombre del topic
            string BuildTopicKey(string baseName) => $"{baseName}{stationName}_{stationId.ToString("D2")}";

            // Diccionario con los nombres de los topics
            // Se concatena el nombre de la estación y el ID en algunos topics
            Dictionary<string, string> topicNames = new Dictionary<string, string>()
            {
                { AlarmsService.C_TopicName, BuildTopicKey(AlarmsService.C_TopicName) },
                { "CurrentStatusSummary", BuildTopicKey("CurrentStatusSummary") },
                { "CurrentStatus", BuildTopicKey("CurrentStatus") },
                { C_LifeBitTopicName, BuildTopicKey(C_LifeBitTopicName) },
                { "ActivePopup", BuildTopicKey("ActivePopup") },
                { "DeviceOutResults", BuildTopicKey("DeviceOutResults") },
                { "MicrowaveProbes", BuildTopicKey("MicrowaveProbes") },
                { NetworkDiagramService.C_TopicName, NetworkDiagramService.C_TopicName },
                { "Measurement", "Measurement" },
                { "LaserMeasurement", "LaserMeasurement" },
            };

            // Para ID_Station 0 (estación de recogida y entrega), publica el CurrentStatusSummary2 y CurrentStatus2
            // Estos topics se utilizan para publicar InputTestplan y OutputTestplan
            if (stationId == 0)
            {
                topicNames.Add("CurrentStatusSummary2", BuildTopicKey("CurrentStatusSummary2"));
                topicNames.Add("CurrentStatus2", BuildTopicKey("CurrentStatus2"));
            }

            return topicNames;
        }

        /// <summary>
        /// Actualiza el topic LifeBit con la hora actual
        /// </summary>
        private void UpdateLifeBitTopicData()
        {
            _topicsData[C_LifeBitTopicName] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        }

        #endregion

        #region MQTT Publish

        /// <summary>
        /// Publica en todos los topics
        /// </summary>
        private async Task PublishTopicsAsync()
        {
            // Actualiza el topic LifeBit
            UpdateLifeBitTopicData();

            foreach (var topic in _topicsData.Keys)
            {
                _topicsData.TryGetValue(topic, out string? data);
                _topicNames.TryGetValue(topic, out string? topicName);

                // Comprueba si el topic o los datos están vacíos
                if (data.IsNullOrEmpty() || topicName.IsNullOrEmpty())
                {
                    // No publica
                    Log.Error("[MQTT] ⚠️ Mqtt: {Topic} topic or data is empty. Skipping...", topic);
                    continue;
                }

                // Publica en el topic
                await _mqttHelper.PublishAsync(topicName!, data!, _cts.Token);
            }
        }

        public static async Task PublishAsync(string payload, string topic = "server/data")
        {
            if (_client == null || !_client.IsConnected)
            {
                Console.WriteLine("[MQTT] ⚠️ Cliente no conectado. Ignorando publicación.");
                return;
            }

            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(Encoding.UTF8.GetBytes(payload))
                .WithRetainFlag(true)
                .Build();

            await _client.PublishAsync(message);
            //Console.WriteLine($"[MQTT] 📤 Publicado en {topic}: {payload}");
        }

        #endregion

        #region Start/Stop

        public void Start()
        {
            _cts = new CancellationTokenSource();

            try
            {
                _ = Task.Run(async () =>
                {
                    // Espera hasta que el cliente se conecte
                    await _mqttHelper.ConnectWithRetryAsync(_cts.Token);

                    while (!_cts.IsCancellationRequested)
                    {
                        try
                        {
                            // Publica todos los topics
                            await PublishTopicsAsync();
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "Error publishing message. Will retry...");
                        }

                        // Espera para la siguiente publicación
                        try
                        {
                            await Task.Delay(_publishInterval, _cts.Token);
                        }
                        catch (TaskCanceledException)
                        {
                            // Salir del bucle si se cancela la tarea
                            break;
                        }
                    }
                });
            }
            catch (TaskCanceledException ex)
            {
                //Para capturar en caso de utilizar el await, pero queremos que ruede la tarea  no que espere
                Log.Error(ex, "TaskCanceledException");
            }

        }

        /// <summary>
        /// Detiene el servicio
        /// </summary>
        public void Stop()
        {
            _cts.Cancel();
            _mqttHelper?.Dispose();
            _cts.Dispose();
        }

        protected virtual void Dispose(bool disposing)
        {
            Stop();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion
    }
}