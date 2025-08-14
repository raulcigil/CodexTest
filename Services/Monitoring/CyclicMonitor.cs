using System.Collections.ObjectModel;
using System.ComponentModel;

namespace TestPlan.Logic.Services.Monitoring
{
    public class CyclicMonitor
    {
        // Clave: (StationId, SlotId)
        public Dictionary<(int StationId, int SlotId), CyclicSlotModel> Slots { get; } = new();

        // Eventos para notificar a la UI
        public event Action<CyclicSlotModel>? SlotAdded;
        public event Action<int, int, VariableSnapshot>? ReadingUpdated;
        public event Action<int, int, VariableSnapshot>? PublicationSent;

        public CyclicSlotModel GetOrCreateSlot(int stationId, int slotId)
        {
            var key = (stationId, slotId);
            if (!Slots.TryGetValue(key, out var slot))
            {
                slot = new CyclicSlotModel
                {
                    StationId = stationId,
                    SlotId = slotId,
                    Readings = new ObservableCollection<VariableSnapshot>(),
                    Publications = new ObservableCollection<VariableSnapshot>()
                };
                Slots[key] = slot;
                SlotAdded?.Invoke(slot);
            }
            return slot;
        }

        public void UpdateReading(int stationId, int slotId, VariableSnapshot reading)
        {
            var slot = GetOrCreateSlot(stationId, slotId);
            ReadingUpdated?.Invoke(stationId, slotId, reading);
        }

        public void UpdatePublication(int stationId, int slotId, VariableSnapshot publication)
        {
            var slot = GetOrCreateSlot(stationId, slotId);
            PublicationSent?.Invoke(stationId, slotId, publication);
        }
    }

    public class CyclicSlotModel
    {
        public int StationId { get; set; }
        public int SlotId { get; set; }
        public ObservableCollection<VariableSnapshot> Readings { get; set; } = new();
        public ObservableCollection<VariableSnapshot> Publications { get; set; } = new();
    }

    public class VariableSnapshot : INotifyPropertyChanged
    {
        private string _name;
        private double? _value;
        private string _unit;
        private DateTime _timestamp;

        public string Name
        {
            get => _name;
            set
            {
                if (_name != value)
                {
                    _name = value;
                    OnPropertyChanged(nameof(Name));
                }
            }
        }

        public double? Value
        {
            get => _value;
            set
            {
                if (_value != value)
                {
                    _value = value;
                    OnPropertyChanged(nameof(Value));
                }
            }
        }
        public string Unit
        {
            get => _unit;
            set
            {
                if (_unit != value)
                {
                    _unit = value;
                    OnPropertyChanged(nameof(Unit));
                }
            }
        }

        public DateTime Timestamp
        {
            get => _timestamp;
            set
            {
                if (_timestamp != value)
                {
                    _timestamp = value;
                    OnPropertyChanged(nameof(Timestamp));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
