using System;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using TestPlan.Entities;
using TestPlan.Entities.Views;
using TestPlan.Logic;
using TestPlan.Logic.Models;
using TestPlan.Logic.Services;
using System.Text.Json;

namespace TestPlan.Logic.Services
{
    /// <summary>
    /// Servicio para comprobar y obtener datos actualizados de la bd cada segundo
    /// </summary>
    public class NetworkDiagramService
    {
        #region Fields

        /// <summary>
        /// token de cancelación
        /// </summary>
        private CancellationTokenSource _cts = new CancellationTokenSource();

        /// <summary>
        /// Intervalo de comprobación
        /// </summary>
        private readonly TimeSpan _interval = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Indica si el servicio esta activo
        /// </summary>
        private bool _isEnabled = false;

        /// <summary>
        /// Diccionario donde se guardan los datos a pulicar en MQTT
        /// </summary>
        private readonly ConcurrentDictionary<string, string> _topicsData;

        /// <summary>
        /// Nombre del topic
        /// </summary>
        public const string C_TopicName = "NetworkDiagram";

        /// <summary>
        /// Lista de dispositivos a comprobar
        /// </summary>
        private readonly List<NetworkDiagram> _devices;

        #endregion

        #region Constructor

        public NetworkDiagramService(ConcurrentDictionary<string, string> topicsData)
        {
            // Diccionario donde se guardan los datos a pulicar en MQTT
            _topicsData = topicsData;

            //Obtener la lista de dispositivos
            using NetworkDiagramLogic netDiagramLO = new NetworkDiagramLogic();
            _devices = netDiagramLO.GetNetworkDiagram();
        }

        #endregion

        #region Auxiliary methods

        /// <summary>
        /// Actualiza los datos para que se publiquen en el topic
        /// </summary>
        /// <param name="data">Resultados de ping como lista de listas</param>
        private void UpdateTopicData(List<List<object>> data)
        {
            // Guarda los datos en el diccionario
            _topicsData[C_TopicName] = JsonSerializer.Serialize(data);
        }

        #endregion

        #region Start / Stop
        /// <summary>
        /// Inicia el servicio
        /// </summary>
        public void Start()
        {
            if (!_isEnabled)
            {
                _cts = new CancellationTokenSource();
                _isEnabled = true;
                //Ejemplo para controlar excepciones en tareas, usar await
                //Si no usas await para comprobarlo debes usar el continueWith y el IsFaulted
                try
                {
                    _ = Task.Run(async () =>
                        {
                            // Guarda los datos en el formato necesario para el topic MQTT
                            var data = new List<List<object>>();

                            while (_isEnabled && !_cts.IsCancellationRequested)
                            {
                                // Intenta hacer ping a todos los dispositivos
                                foreach (var device in _devices)
                                {
                                    var result = await PingHelper.PingAsync(device.IP);
                                    data.Add([device.Name, device.IP, result.Success, result.RoundtripTime]);
                                }

                                // Actualiza los datos para que se publiquen en el topic
                                UpdateTopicData(data);
                                data.Clear();

                                // Espera antes del siguiente ciclo de ping
                                await Task.Delay(_interval, _cts.Token);
                            }
                        }).ContinueWith((task) =>
                        {
                            if (task.IsFaulted)
                            {
                                if (task.Exception?.InnerException != null)
                                {
                                    Log.Error("NetworkDiagramService errors: {Message}", task.Exception.InnerException.Message);
                                }
                                else
                                {
                                    Log.Error("NetworkDiagramService encountered an error but no details are available");
                                }
                            }
                        }
                    );
                }
                catch (TaskCanceledException ex)
                {
                    //Para capturar en caso de utilizar el await, pero queremos que ruede la tarea  no que espere
                    Log.Error(ex, "TaskCanceledException");
                }
                Log.Debug("NetworkDiagramService Start");
            }
        }

        /// <summary>
        /// Detiene el servicio
        /// </summary>
        public void Stop()
        {
            _isEnabled = false;
            _cts.Cancel();
        }

        #endregion
    }
}