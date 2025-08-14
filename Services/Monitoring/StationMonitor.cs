using System.Collections.Concurrent;
using TestPlan.Logic.Models;

public class StationMonitor
{
    #region Properties
    private readonly ConcurrentDictionary<int, StationModel> _stationStatuses = new();
    private readonly ConcurrentDictionary<(int stationId, int slotId), SlotModel> _slotStatuses = new();
    #endregion

    #region Monitorización de Stations
    // STATION MONITORING
    public void UpdateStationStatus(int stationId, StationModel.StationState state, string message = "")
    {
        _stationStatuses.AddOrUpdate(
            stationId,
            new StationModel
            {
                StationId = stationId,
                State = state.ToString(),
                Message = message,
                LastUpdated = DateTime.Now
            },
            (id, existing) =>
            {
                existing.State = state.ToString();
                existing.Message = message;
                existing.LastUpdated = DateTime.Now;
                return existing;
            });
    }
    #endregion

    #region Monitorizacion de uns Slot
    // SLOT MONITORING
    public void UpdateSlotStatus(int stationId, int slotId, SlotModel.SlotState state, string message = "")
    {
        var key = (stationId, slotId);
        _slotStatuses.AddOrUpdate(
            key,
            new SlotModel
            {
                StationId = stationId,
                SlotId = slotId,
                State = state.ToString(),
                Message = message,
                LastUpdated = DateTime.Now
            },
            (k, existing) =>
            {
                existing.State = state.ToString();
                existing.Message = message;
                existing.LastUpdated = DateTime.Now;
                return existing;
            });
    }
    #endregion

    #region Funciones para compartir con el MVVM el status y bindear al front
    public IReadOnlyCollection<StationModel> GetAllStationStatuses() =>
    _stationStatuses.Values.ToList();
    public IReadOnlyCollection<SlotModel> GetAllSlotStatuses() =>
        _slotStatuses.Values.ToList();
    #endregion
}