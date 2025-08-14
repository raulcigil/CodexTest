using MQTTnet;
using MQTTnet.Diagnostics.Logger;
using MQTTnet.Implementations;
using Newtonsoft.Json;
using Serilog;
using System.Text;
using TestPlan.Logic.Configuration;
using TestPlan.Logic.ContinuosReading.Eventos.Messages;
using TestPlan.Logic.Interfaces;

namespace TestPlan.Logic.Services.Mqtt
{
    public static class MqttSubscriber
    {
        #region Suscripción con Eventos asignados
        public static async Task InitAsync(IEventAggregator eventAggregator, string topic)
        {
            var logger = new MqttNetNullLogger();
            var adapterFactory = new MqttClientAdapterFactory();
            var mqttClient = new MqttClient(adapterFactory, logger);
            var options = new MqttClientOptionsBuilder()
                .WithTcpServer(AppConfig.Mqtt.Broker, AppConfig.Mqtt.PublishPort)
                .WithCleanSession()
                .Build();

            #region Event Handlers
            mqttClient.ApplicationMessageReceivedAsync += e =>
            {
                var payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);
                Log.Information($"[MQTT] 📩 Payload recibido: {payload}");

                try
                {
                    var baseObj = JsonConvert.DeserializeObject<BaseEventDto>(payload);

                    if (baseObj?.Type == null)
                    {
                        Log.Information("[MQTT] ❗ Tipo de evento nulo o no válido");
                        return Task.CompletedTask;
                    }

                    Log.Information($"[MQTT] 🔁 Evento tipo: {baseObj.Type}");

                    switch (baseObj.Type)
                    {
                        case "StartMonitoringEvent":
                            var start = JsonConvert.DeserializeObject<StartMonitoringEvent>(payload);
                            Log.Information($"[MQTT] ▶️ Lanzando StartMonitoring para {start.TargetDeviceId}");
                            eventAggregator.Publish(start);
                            break;

                        case "StopMonitoringEvent":
                            var stop = JsonConvert.DeserializeObject<StopMonitoringEvent>(payload);
                            Log.Information($"[MQTT] ⏹ Lanzando StopMonitoring para {stop.TargetDeviceId}");
                            eventAggregator.Publish(stop);
                            break;

                        case "StartTriggerActionEvent":
                            var trig = JsonConvert.DeserializeObject<StartStartTriggerActionEvent>(payload);
                            Log.Information($"[MQTT] ⚙️ Lanzando TriggerAction para {trig.TargetDeviceId}");
                            eventAggregator.Publish(trig);
                            break;

                        case "StopTriggerActionEvent":
                            var stopAction = JsonConvert.DeserializeObject<StopTriggerActionEvent>(payload);
                            Log.Information($"[MQTT] ⚙️ Lanzando TriggerAction para {stopAction.TargetDeviceId}");
                            eventAggregator.Publish(stopAction);
                            break;

                        default:
                            Log.Information($"[MQTT] ❓ Tipo de evento no reconocido: {baseObj.Type}");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Log.Information($"[MQTT] ❌ Error deserializando: {ex.Message}");
                }

                return Task.CompletedTask;
            };
            #endregion

            await mqttClient.ConnectAsync(options);
            await mqttClient.SubscribeAsync(topic);
            Log.Information($"[MQTT] Suscrito a {topic}");
        }
        #endregion

        #region Tipo el cual se deserializa el mensaje y se usa para determinar el tipo de evento
        class BaseEventDto
        {
            public string Type { get; set; }
        }
        #endregion
    }
}
