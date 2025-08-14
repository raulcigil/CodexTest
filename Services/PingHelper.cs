using System;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using TestPlan.Logic.Models;

namespace TestPlan.Logic.Services
{
    /// <summary>
    /// Clase auxiliar para realizar pings
    /// </summary>
    public static class PingHelper
    {
        /// <summary>
        /// Realiza un ping a una dirección IP
        /// </summary>
        /// <param name="ipAddress">Direccion IP</param>
        /// <param name="timeout">Tiempo máximo de espera</param>
        /// <returns>Devuelve un PingResultModel con el resultado</returns>
        public static async Task<PingResultModel> PingAsync(string ipAddress, int timeout = 500)
        {
            // Validaciones
            // Verifica que la dirección IP no sea vacía
            if (string.IsNullOrWhiteSpace(ipAddress))
            {
                throw new ArgumentException("IP address cannot be empty", nameof(ipAddress));
            }
            
            // Verifica que el tiempo de espera sea mayor que 0
            if (timeout <= 0)
            {
                throw new ArgumentException("Timeout must be greater than 0", nameof(timeout));
            }

            // Crea el objeto PingResultModel
            var result = new PingResultModel
            {
                IpAddress = ipAddress,
                Timestamp = DateTime.UtcNow
            };

            try
            {
                // Realiza el ping
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(ipAddress, timeout);

                // Guarda el resultado
                result.Success = reply.Status == IPStatus.Success;
                result.Status = reply.Status.ToString();
                result.RoundtripTime = reply.Status == IPStatus.Success ? reply.RoundtripTime : -1;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Status = $"Error: {ex.Message}";
                result.RoundtripTime = -1;
            }

            return result;
        }
    }
}