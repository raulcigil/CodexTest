using Serilog;
using TestPlan.Logic.Interfaces;
using TestPlan.Logic.Models;

namespace TestPlan.Logic.Services.IdentifyMode
{
    /// <summary>
    /// Force Order Class
    /// </summary>
    internal class ForceOrderIdentifyMode : IIdentifyMode
    {
        //Servicio 
        private DummyService _dummyService = new DummyService();

        /// <summary>
        /// Timeout operation
        /// </summary>
        private const int C_Timeout = 5000;

        /// <summary>
        /// Init Cycle data
        /// </summary>
        private CycleDataModel _startCycleData;
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="startCycleData"></param>
        public ForceOrderIdentifyMode(CycleDataModel startCycleData)
        {
            _startCycleData = startCycleData;
        }

        /// <summary>
        /// Get Cycle data needed for start testPlan  ( Procedure SimulateSNR in the database )
        ///     1. Force Order have the SNR in the database forced in the HMI - Charge this SNR.
        ///     2. Don't have SNR in the Database. We need to create a new SNR, not worked from this order.
        ///         2.1 There SNR usables in the ORDERNR.
        ///         2.2 We used all the SNR that we ahve in the ORDERNR, in this case we asign last numbre of SNR in the ORDERNR
        /// </summary>
        /// <returns> CycleDataModel cycle data </returns>
        public async Task<CycleDataModel> IdentifyAsync()
        {
            try
            {
                // Make call to the procedure that checks the SNR for the FORCE_ORDER mode
                // Procedure returns the SNR only 
                _startCycleData.IsDummy = false;
                using DefinitionCyclemodeLogic definitionCyclemodeLogic = new DefinitionCyclemodeLogic();
                _startCycleData.SNR = definitionCyclemodeLogic.Simulatesnr(_startCycleData.StationId);

                return _startCycleData;
            }
            catch (Exception e)
            {
                Log.Error("Error: {0}", e.Message);
                throw e;
            }
        }
    }
}
