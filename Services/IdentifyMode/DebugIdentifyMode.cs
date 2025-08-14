using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Serilog;
using TestPlan.Logic.Interfaces;
using TestPlan.Logic.Models;

namespace TestPlan.Logic.Services.IdentifyMode
{
    /// <summary>
    /// 
    /// </summary>
    internal class DebugIdentifyMode : IIdentifyMode
    {
        /// <summary>
        /// Timeout de la operación
        /// </summary>
        private const int C_Timeout = 5000;

        /// <summary>
        /// Datos de inicio de ciclo
        /// </summary>
        private CycleDataModel _startCycleData;
        /// <summary>
        /// ctor
        /// </summary>
        /// <param name="startCycleData"></param>
        public DebugIdentifyMode(CycleDataModel startCycleData)
        {
            _startCycleData = startCycleData;
        }
        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        public async Task<CycleDataModel> IdentifyAsync()
        {
            //DEBUG - Simulamos el código de barras
            _startCycleData.SNR = "984121035024000090";
            return _startCycleData;
        }
    }
}
