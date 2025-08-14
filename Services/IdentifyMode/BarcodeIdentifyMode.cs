using System.Net.Sockets;
using System.Text;
using Serilog;
using TestPlan.Logic.Interfaces;
using TestPlan.Logic.Models;

namespace TestPlan.Logic.Services.IdentifyMode
{
    /// <summary>
    /// 
    /// </summary>
    internal class BarcodeIdentifyMode : IIdentifyMode
    {
        /// <summary>
        /// Lógica de negocio para todo lo que tiene que ver con Dummy
        /// </summary>
        private DummyService _dummyService = new DummyService();

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
        public BarcodeIdentifyMode(CycleDataModel startCycleData)
        {
            _startCycleData = startCycleData;
        }
        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        public async Task<CycleDataModel> IdentifyAsync()
        {
            string server = Config.Instance.General.UartConfig.DeviceConfig.IP; // Dirección IP del servidor
            int port = Config.Instance.General.UartConfig.DeviceConfig.Puerto; // Puerto del servidor
            server = "127.0.0.1";
            port = 8888;

            bool isCompleted = false;

            CycleDataModel startCycleData = null;

            try
            {

            // Crear un cliente TCP y conectarse al servidor
            TcpClient client = new TcpClient(server, port);

            // Obtener el stream para enviar y recibir datos
            NetworkStream stream = client.GetStream();

            //ACTIVAR lectura de código de barras
            string message = "||>trigger on\r\n";
            byte[] data = Encoding.ASCII.GetBytes(message);
            // Enviar el mensaje al servidor
            isCompleted = stream.WriteAsync(data, 0, data.Length).Wait(C_Timeout);
            if (!isCompleted) throw new Exception("Timeout");

            Log.Debug("BarcodeReader - activate reader/ {0}", message);

            // Buffer para almacenar la respuesta del servidor
            data = new byte[256];
            string responseData = string.Empty;

            // Leer la respuesta del servidor
            //int bytes = await stream.ReadAsync(data, 0, data.Length);
            Task<int> t = stream.ReadAsync(data, 0, data.Length);
            isCompleted = t.Wait(C_Timeout);
            if (!isCompleted) throw new Exception("Timeout");

            int bytes = t.Result;
            responseData = Encoding.ASCII.GetString(data, 0, bytes);
            //Asignamos el código de barras
            _startCycleData.SNR = responseData;

            Log.Debug("BarcodeReader - received barcode/ {0}", responseData);

            //DESACTIVAR lectura de código de barras
            //message = "555";
            //data = Encoding.ASCII.GetBytes(message);
            //// Enviar el mensaje al servidor
            //isCompleted = stream.WriteAsync(data, 0, data.Length).Wait(C_Timeout);
            //if (!isCompleted) throw new Exception("Timeout");

            Log.Debug("BarcodeReader - deactivate reader/ {0}", message);

            // Cerrar el stream y el cliente
            stream.Close();
            client.Close();

            }
            catch (Exception e)
            {
                Log.Error("Error: {0}", e.Message);
                throw e;
            }

            //DEBUG - Simulamos el código de barras (bueno de la BD)
            _startCycleData.SNR = "984121035024000090";
            return _startCycleData;
        }
    }
}
