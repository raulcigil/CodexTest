using System;
using MQTTnet;
using Serilog;
using TestPlan.Logic.Configuration;
using TestPlan.Logic.Models;

namespace TestPlan.Logic.Services.Mqtt
{
    public class MqttHelper : IDisposable
    {
        #region Fields
        private readonly IMqttClient _mqttClient;
        private readonly MqttConfigModel _config;
        #endregion

        #region Properties
        public bool IsConnected => _mqttClient?.IsConnected ?? false;
        #endregion

        #region Constructor
        public MqttHelper(MqttConfigModel config)
        {
            _config = config;

            // Crea el cliente MQTT
            var factory = new MqttClientFactory();
            _mqttClient = factory.CreateMqttClient();
        }
        #endregion

        #region Conections
        /// <summary>
        /// Establece una conexión con el broker MQTT
        /// </summary>
        public async Task ConnectAsync()
        {            
            var options = new MqttClientOptionsBuilder()
                .WithTcpServer(_config.BrokerAddress, _config.BrokerPort)
                .WithCleanSession()
                .Build();
            await _mqttClient.ConnectAsync(options);
            Log.Information($"[MQTT] The MQTT client is connected to {_config.BrokerAddress}:{_config.BrokerPort}:{_config.ClientId}");
        }

        /// <summary>
        /// Método auxiliar para reconexión con reintentos.
        /// El tiempo de reintentos es exponencial hasta un máximo de 20 segundos
        /// </summary>
        public async Task ConnectWithRetryAsync(CancellationToken cts)
        {
            // Tiempo inicial entre reintentos
            const int InitialDelaySeconds = 1;
            // Tiempo máximo entre reintentos
            const int MaxDelaySeconds = 20; // Reintentos: 1, 2, 4, 8, 16, 20, 20, 20 segundos
            int retryAttempt = 0;

            // Mientras el cliente no se haya conectado y no se haya cancelado la tarea
            while ((_mqttClient == null || !_mqttClient.IsConnected) && !cts.IsCancellationRequested)
            {
                try
                {
                    // Intenta la conexión
                    await ConnectAsync();
                    Log.Information("[MQTT] Connected to MQTT broker at {BrokerAddress}:{BrokerPort}:{ID}", _config.BrokerAddress, _config.BrokerPort,_config.ClientId);
                }
                catch (Exception ex)
                {
                    // Calcula el tiempo de espera para el siguiente reintentos
                    int delay = Math.Min(InitialDelaySeconds * (int)Math.Pow(2, retryAttempt), MaxDelaySeconds);
                    retryAttempt++;
                    Log.Error(ex, "[MQTT] Error connecting to MQTT broker. Retrying in {Seconds} seconds...", delay);

                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(delay), cts);
                    }
                    catch (TaskCanceledException)
                    {
                        // Si se cancela, salimos del bucle
                        break;
                    }
                }
            }
        }


        /// <summary>
        /// Publica un mensaje MQTT
        /// </summary>
        /// <remarks>
        /// Información opciones QoS:
        ///     - AtMostOnce: sin garantías, el más eficiente
        ///     - AtLeastOnce: entrega garantizada al menos una vez (pueden haber duplicados), eficiencia media
        ///     - ExactlyOnce: entrega grantizada exactamente una vez, menor eficiencia
        /// </remarks>
        /// <param name="topic">Nombre del topic</param>
        /// <param name="data">Datos del mensaje</param>
        /// <param name="retain">Retener el mensaje</param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException"></exception>
        public async Task PublishAsync(string topic, string data, CancellationToken cts, bool retain = true, MQTTnet.Protocol.MqttQualityOfServiceLevel qos = MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce)
        {
            // Comprueba si el cliente se ha desconectado
            if (_mqttClient == null || !_mqttClient.IsConnected)
            {
                await ConnectWithRetryAsync(cts);
            }

            // Crea un mensaje MQTT
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(data)
                .WithRetainFlag(retain)
                .WithQualityOfServiceLevel(qos)
                .Build();

            // Publica el mensaje
            await _mqttClient!.PublishAsync(message, cts);
        }

        #endregion

        #region Dispose

        protected virtual void Dispose(bool disposing)
        {
            // Comprueba si el cliente está conectado
            if (_mqttClient != null && _mqttClient.IsConnected)
            {
                // Desconecta el cliente
                var disconnectOptions =
                    new MqttClientDisconnectOptionsBuilder().WithReason(MqttClientDisconnectOptionsReason.NormalDisconnection).Build();
                _mqttClient.DisconnectAsync(disconnectOptions);
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion
    }
}
