using System.Collections.ObjectModel;
using System.ComponentModel;
using Autis.DBUS2;
using System.Diagnostics;
using TestPlan.Logic.Models.Dbus2;

namespace TestPlan.Logic.Services.Monitoring
{
    public class DBUSMonitor
    {
        #region  Properties / Dispacher / Events
        public Dictionary<(int StationId, int SlotId), DBUSConnectionModel> Connections { get; } = new();

        // Nuevo: notificación al ViewModel
        public event Action<DBUSConnectionModel>? ConnectionAdded;
        public event Action<int, int, MessageContent>? MessageReceived;
        #endregion

        #region Connection
        public DBUSConnectionModel GetOrCreateConnection(int stationId, int slotId)
        {
            var key = (stationId, slotId);
            if (!Connections.TryGetValue(key, out var model))
            {
                model = new DBUSConnectionModel
                {
                    StationId = stationId,
                    SlotId = slotId,
                    UDA_IP = "192.168.0.100",
                    Password = "default",
                    Baudrate = 9600,
                    RequestMessageID = "01",
                    ResponseTimeout = 1000,
                    Messages = new ObservableCollection<MessageContent>(),
                    IsConnected = false,
                    UdaInstance = null
                };

                Connections[key] = model;
                ConnectionAdded?.Invoke(model); // 🔔 Notificar al ViewModel
            }

            return model;
        }

        public bool ConnectToUDA(int stationId, int slotId)
        {
            var model = GetOrCreateConnection(stationId, slotId);

            model.UdaInstance ??= new UDA2(model.UDA_IP, model.Password, (uint)model.Baudrate, 0);

            var result = model.UdaInstance.OpenEth();
            if (result != eUdaResult.Success)
            {
                AddMessage(model, "❌ Error al conectar con el UDA.");
                return false;
            }

            var obsResult = model.UdaInstance.OpenObserver();
            if (obsResult == eUdaResult.Success)
            {
                model.IsConnected = true;
                HookToUdaEvents(model);
                AddMessage(model, "✅ Conexión establecida y modo observador activado.");
            }
            else
            {
                AddMessage(model, "⚠️ Conexión establecida, pero falló el modo observador.");
            }

            return obsResult == eUdaResult.Success;
        }
        #endregion

        #region Commands
        public void DisconnectUDA(int stationId, int slotId)
        {
            var model = GetOrCreateConnection(stationId, slotId);
            model.UdaInstance?.Close();
            model.IsConnected = false;
            AddMessage(model, "🔌 Desconectado del UDA.");
        }

        public void SendFrame(int stationId, int slotId, byte targetAddress, byte[] data)
        {
            var model = GetOrCreateConnection(stationId, slotId);
            if (model.UdaInstance == null || !model.IsConnected)
            {
                AddMessage(model, "❌ UDA no conectado.");
                return;
            }

            var result = model.UdaInstance.SendMessage(targetAddress, data, (uint)model.ResponseTimeout, 1);
            if (result == eUdaResult.Success)
            {
                AddMessage(model, $"📤 Frame enviado a 0x{targetAddress:X2}: {BitConverter.ToString(data)}");
            }
            else
            {
                AddMessage(model, $"❌ Error al enviar frame: {result}");
            }
        }

        public void TrackExternalUDA2(UDADevice uda, int stationId, int slotId, string ip, string password, uint baudrate)
        {
            var key = (stationId, slotId);
            DBUSConnectionModel model;

            if (!Connections.TryGetValue(key, out model))
            {
                model = new DBUSConnectionModel
                {
                    StationId = stationId,
                    SlotId = slotId,
                    UDA_IP = ip,
                    Password = password,
                    Baudrate = baudrate,
                    Messages = new ObservableCollection<MessageContent>(),
                    IsConnected = uda.Uda.State is eUdaState.Connected or eUdaState.Observer,
                    UdaInstance = uda.Uda
                };

                Connections[key] = model;
                ConnectionAdded?.Invoke(model); // Notificar MVVM
            }
            else
            {
                model.UdaInstance = uda.Uda;
                model.IsConnected = uda.Uda.State is eUdaState.Connected or eUdaState.Observer;
            }

            if (model.IsConnected = uda.Uda.State is eUdaState.Connected or eUdaState.Observer)
            {
                HookToUdaEvents(model);
            }
        }
        #endregion

        #region Message Helpers
        private void HookToUdaEvents(DBUSConnectionModel model)
        {
            if (model.UdaInstance == null) return;

            model.UdaInstance.FrameReceived += (s, frame) =>
            {
                try
                {
                    var message = new MessageContent
                    {
                        Timestamp = frame.timestamp,
                        TargetAddress = frame.targetAddress.ToString("X2"),
                        AckType = ((byte)frame.ackType).ToString("X2"), // 👈 CAST explícito
                        AckSender = frame.ackCommPartner.ToString("X2"),
                        MessageSize = frame.messageSize,
                        CRC = ((byte)frame.Direction).ToString("X2"),    // 👈 también es enum
                        MessageId = frame.checkSum.ToString("X2"),
                        MessageData = BitConverter.ToString(frame.messageBuffer).Replace("-", " ")
                    };

                    model.Messages.Add(message);
                    MessageReceived?.Invoke(model.StationId, model.SlotId, message);
                }
                catch (Exception ex)
                {
                    // ⚠️ IMPORTANTE: no relances la excepción, o se rompe el hilo del handler
                    Debug.WriteLine($"[DBUS] Error al parsear frame: {ex.Message}");
                }
            };

            model.UdaInstance.TimeOut += (s, _) =>
            {
                model.Messages.Add(new MessageContent
                {
                    Timestamp = DateTime.Now,
                    MessageData = $"⏱️ Timeout esperando frame"
                });
            };
        }

        private void AddMessage(DBUSConnectionModel model, string message)
        {
            model.Messages.Add(new MessageContent
            {
                Timestamp = DateTime.Now,
                MessageData = message
            });
        }
        #endregion
    }

    public class DBUSConnectionModel : INotifyPropertyChanged
    {
        public int StationId { get; set; }
        public int SlotId { get; set; }

        public string UDA_IP { get; set; }
        public string Password { get; set; }
        public uint Baudrate { get; set; }

        public string RequestMessageID { get; set; }
        public int ResponseTimeout { get; set; }

        public ObservableCollection<MessageContent> Messages { get; set; }

        public IDbus2? UdaInstance { get; set; }

        private bool _isConnected;
        public bool IsConnected
        {
            get => _isConnected;
            set { _isConnected = value; OnPropertyChanged(nameof(IsConnected)); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class MessageContent : INotifyPropertyChanged
    {
        public DateTime Timestamp { get; set; }
        public string TargetAddress { get; set; }
        public string AckType { get; set; }
        public string AckSender { get; set; }
        public int MessageSize { get; set; }
        public string CRC { get; set; }
        public string MessageId { get; set; }
        public string MessageData { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
