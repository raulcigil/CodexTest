using System.Net;
using System.Net.Sockets;
using System.Text;
using Serilog;
using TestPlan.Entities.Enumeraciones;
using TestPlan.Entities.JSONParameters.TCPTelegram;


namespace TestPlan.Logic.Services.TCP
{
    /// <summary>
    /// Servicio para manejar las conexiones TCP
    /// </summary>
    public class TCPModule : IDisposable
    {
        /// <summary>
        /// Telegrama enviado 
        /// </summary>
        public byte[] SentTelegram { get; set; }

        /// <summary>
        /// Parámetros de configuración del servicio TCP
        /// </summary>
        private Lazy<TCPTelegramJson?> _Parameters;

        /// <summary>
        /// Instancia del Cliente TCP
        /// </summary>
        private TcpClient? _client;

        /// <summary>
        /// Stream de red para la comunicación TCP
        /// </summary>
        private NetworkStream? _stream;

        /// <summary>
        /// Constructor de la clase TCPModule
        /// </summary>
        /// <param name="parametersJson">Recibe un Lazy<TCPTelegramJson></param>
        public TCPModule(Lazy<TCPTelegramJson?> parametersJson)
        {
            if (parametersJson == null)
            {
                ArgumentNullException.ThrowIfNull(parametersJson);
                Log.Error("[TCP] No se han cargado parámetros para el Modulo TCP");
            }
            _Parameters = parametersJson;
        }

        /// <summary>
        /// /EMpty constructor
        /// </summary>
        /// <param name="parametersJson"></param>
        public TCPModule(){}

        /// <summary>
        /// Obtener los parámetros de la acción (Parameters)
        /// </summary>
        public Parameters? Parameters
        {
            get
            {
                if (_Parameters.Value == null)
                {
                    return null;
                }
                return _Parameters.Value.Parameters;
            }
        }

        /// <summary>
        /// Obtener los parámetros hijos de la acción (ChildParameters)
        /// </summary>
        public ChildParameters? ChildParameters
        {
            get
            {
                if (_Parameters.Value == null)
                {
                    return null;
                }
                return _Parameters.Value.ChildParameters;
            }
        }

        /// <summary>
        /// Inicia la conexión TCP con el servidor especificado por la IP y el puerto y obtiene el stream de red.
        /// </summary>
        /// <param name="ip"> Ip</param>
        /// <param name="port">Puerto</param>
        /// <returns>true si se inicializa correctamente la conexión, false si no</returns>
        public bool StartConnection(string ip, int? port)
        {
            try
            {
                Log.Information($"Starting TCP connection to {ip}:{port}");

                _client = new TcpClient(AddressFamily.InterNetwork);
                var result = _client.BeginConnect(ip, port.Value, null, null);
                bool success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(5));

                if (!success)
                {
                    throw new SocketException((int)SocketError.TimedOut);
                }

                _client.EndConnect(result);
                _stream = _client.GetStream();

                Log.Information($"TCP connection established to {ip}:{port}");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error establishing TCP communication");
                _stream = null;
                return false;
            }
        }

        /// <summary>
        /// Construye y envía el telegrama al servidor TCP.
        /// </summary>
        /// <param name="stream">Stream de red obtenido en StartConnection</param>
        public void SendTelegram()
        {
            Log.Information("Building and sending telegram");
            if (_stream != null)
            {
                
                var telegram = BuildTelegram(Parameters);
                SentTelegram = telegram;
                // Enviar telegrama
                _stream.Write(telegram, 0, telegram.Length);
                Log.Information($"Telegram sent: {Convert.ToHexString(telegram)}");
            }
            else
            {
                Log.Error("Error: TCP stream is null");
            }
        }
        /// <summary>
        /// Construye el telegrama a enviar al servidor TCP según los parámetros configurados en el JSON.
        /// </summary>
        /// <param name="parameters">Parámetros de TCPTelegramJson</param>
        /// <returns>El telegrama completo como array de bytes</returns>
        public static byte[] BuildTelegram(Parameters parameters)
        {
            var bytes = new List<byte>();

            Log.Information("Building telegram...");
            // Añadir la cabecera al telegrama si está definida (en formato hexadecimal)
            if (!string.IsNullOrEmpty(parameters.HexTxHeader))
                bytes.AddRange(Convert.FromHexString(parameters.HexTxHeader.Replace(" ", "")));

            // Añadir el cuerpo del telegrama:
            if (!string.IsNullOrEmpty(parameters.TxTelegram))
                bytes.AddRange(Encoding.ASCII.GetBytes(parameters.TxTelegram));

            // Añadir la cola al telegrama si está definida (en formato hexadecimal)
            if (!string.IsNullOrEmpty(parameters.HexTxEot))
                bytes.AddRange(Convert.FromHexString(parameters.HexTxEot.Replace(" ", "")));

            // Devolver el telegrama completo como array de bytes

            Log.Information($"Telegram buit: {bytes.ToArray()}");
            return bytes.ToArray();
        }

        /// <summary>
        /// Convierte una cadena hexadecimal a su representación en texto ASCII.
        /// Por ejemplo, "48656C6C6F" se convierte en "Hello".
        /// </summary>
        /// <param name="hex">Cadena en formato hexadecimal (puede contener espacios opcionalmente).</param>
        /// <returns>Cadena en texto ASCII equivalente, o string vacío si la entrada es nula o vacía.</returns>
        public static string ConvertHexToString(string? hex)
        {
            // Si la cadena es nula o vacía, devuelve una cadena vacía.
            if (string.IsNullOrEmpty(hex))
                return string.Empty;

            // Elimina los espacios y convierte la cadena hexadecimal a un array de bytes.
            var bytes = Convert.FromHexString(hex.Replace(" ", ""));

            // Convierte el array de bytes a una cadena ASCII y la devuelve.
            return Encoding.ASCII.GetString(bytes);
        }


        /// <summary>
        /// Valida la respuesta recibida del servidor TCP según los parámetros de validación configurados.
        /// Permite buscar cabecera y cola, extraer un segmento relevante y comparar en modo texto o hexadecimal.
        /// </summary>
        /// <param name="buffer">Buffer de bytes recibido como respuesta.</param>
        /// <param name="length">Número de bytes válidos en el buffer.</param>
        /// <param name="parameters"> Parámetros de la conexión TCP</param>
        /// <param name="devType">Tipo de dispositivo (action o barcode) para aplicar validaciones específicas.</param>
        /// <returns>True si la respuesta es válida según los criterios configurados, false en caso contrario.</returns>
        public static bool ValidateResponse(byte[] buffer, int length, Parameters parameters, eTCPDeviceType devType)
        {
            Log.Information($"Validating response... type {devType}");
            bool isValid;
            switch (devType)
            {
                case eTCPDeviceType.Action:
                    // Si el tipo de dispositivo es "action", se utiliza la validación estándar.
                    isValid = ValidateResponseAction(buffer, length, parameters);
                    break;
                case eTCPDeviceType.Barcode:
                    // Si el tipo de dispositivo es "barcode", se utiliza la validación específica para códigos de barras.
                    isValid = ValidateResponseBarcode(buffer, length, parameters);
                    break;
                default:
                    // Si el tipo de dispositivo no es reconocido, se considera inválido.
                    isValid = false;
                    break;

            }
            Log.Information($"Validation result: {isValid}");
            return isValid;
        }

        /// <summary>
        /// Valida la respuesta del dispositivo según los parámetros configurados cuando el dispositivo es una acción.
        /// </summary>
        /// <param name="buffer">Buffer de bytes recibido como respuesta.</param>
        /// <param name="length">Número de bytes válidos en el buffer.</param>
        /// <param name="parameters"> Parámetros de la conexión TCP</param>
        /// <returns></returns>
        public static bool ValidateResponseAction(byte[] buffer, int length, Parameters parameters)
        {
            // Convierte el buffer de bytes recibido a una cadena ASCII.
            var response = Encoding.ASCII.GetString(buffer, 0, length);
            Console.WriteLine($"response: {response}");

            // Inicializa los índices de inicio y fin para extraer la parte relevante de la respuesta.
            int startIdx = 0;
            int endIdx = response.Length;

            // Si se ha definido una cabecera (header) en hexadecimal, busca su posición en la respuesta.
            // Si la encuentra, ajusta el índice de inicio justo después de la cabecera.
            if (!string.IsNullOrEmpty(parameters.HexRxHeader))
            {
                var header = ConvertHexToString(parameters.HexRxHeader);
                startIdx = response.IndexOf(header, StringComparison.Ordinal);
                if (startIdx < 0) startIdx = 0;
                else startIdx += header.Length;
            }

            // Si se ha definido una cola (EOT) en hexadecimal, busca su posición en la respuesta.
            // Si la encuentra, ajusta el índice de fin justo antes de la cola.
            if (!string.IsNullOrEmpty(parameters.HexRxEot))
            {
                var eot = ConvertHexToString(parameters.HexRxEot);
                endIdx = response.IndexOf(eot, startIdx, StringComparison.Ordinal);
                if (endIdx < 0) endIdx = response.Length;
            }

            // Extrae la parte relevante de la respuesta entre la cabecera y la cola.
            var relevant = response.Substring(startIdx, endIdx - startIdx);

            // Si se han definido los parámetros RxValidationStart y RxValidationLength,
            // extrae el segmento correspondiente dentro de la parte relevante.
            if (parameters.RxValidationStart > 0 && parameters.RxValidationStart < relevant.Length)
            {
                int len = parameters.RxValidationLength > 0
                    ? Math.Min(parameters.RxValidationLength, relevant.Length - parameters.RxValidationStart)
                    : relevant.Length - parameters.RxValidationStart;
                relevant = relevant.Substring(parameters.RxValidationStart, len);
            }

            Console.WriteLine($"relevant: {relevant}");

            // Valida la respuesta:
            // - Si HexRxValidationTelegram está activo, convierte el valor esperado de hexadecimal a texto y compara.
            // - Si no, compara directamente el valor esperado como texto.
            if (parameters.HexRxValidationTelegram)
            {
                var expected = ConvertHexToString(parameters.RxValidationTelegram);
                return relevant.Contains(expected);
            }
            else
            {
                return relevant.Contains(parameters.RxValidationTelegram);
            }
        }

        /// <summary>
        /// Valida la respuesta del dispositivo según los parámetros configurados cuando el dispositivo es un barcode.
        /// </summary>
        /// <param name="buffer">Buffer de bytes recibido como respuesta.</param>
        /// <param name="length">Número de bytes válidos en el buffer.</param>
        /// <param name="parameters"> Parámetros de la conexión TCP</param>
        /// <returns></returns>
        public static bool ValidateResponseBarcode(byte[] buffer, int length, Parameters parameters)
        {

            // Convierte el buffer de bytes recibido a una cadena ASCII.
            var response = Encoding.ASCII.GetString(buffer, 0, length);

            // Inicializa los índices de inicio y fin para extraer la parte relevante de la respuesta.
            int startIdx = 0;
            int endIdx = response.Length;

            // Si se ha definido una cabecera (header) en hexadecimal, busca su posición en la respuesta.
            if (!string.IsNullOrEmpty(parameters.HexRxHeader))
            {
                var header = ConvertHexToString(parameters.HexRxHeader);
                startIdx = response.IndexOf(header, StringComparison.Ordinal);
                if (startIdx < 0) startIdx = 0;
                else startIdx += header.Length;
            }

            // Si se ha definido una cola (EOT) en hexadecimal, busca su posición en la respuesta.
            if (!string.IsNullOrEmpty(parameters.HexRxEot))
            {
                var eot = ConvertHexToString(parameters.HexRxEot);
                endIdx = response.IndexOf(eot, startIdx, StringComparison.Ordinal);
                if (endIdx < 0) endIdx = response.Length;
            }

            // Extrae la parte relevante de la respuesta entre la cabecera y la cola.
            var relevant = response.Substring(startIdx, endIdx - startIdx);

            // Si se han definido los parámetros RxValidationStart y RxValidationLength,
            // extrae el segmento correspondiente dentro de la parte relevante.
            if (parameters.RxValidationStart > 0 && parameters.RxValidationStart < relevant.Length)
            {
                int len = parameters.RxValidationLength > 0
                    ? Math.Min(parameters.RxValidationLength, relevant.Length - parameters.RxValidationStart)
                    : relevant.Length - parameters.RxValidationStart;
                relevant = relevant.Substring(parameters.RxValidationStart, len);
            }

            // La condición de validación: exactamente 16 caracteres
            return relevant.Length == 16;
        }


        /// <summary>
        /// Lee la respuesta de un NetworkStream según el modo de lectura configurado en ChildParameters.ReadMode.
        /// Soporta modos: standard, buffered, CRLF y immediate.
        /// </summary>
        /// <param name="stream">Stream de red desde el que leer los datos.</param>
        /// <param name="buffer">Buffer donde almacenar los datos leídos.</param>
        /// <param name="bytesRead">Salida: número de bytes leídos.</param>
        /// <returns>Número de bytes leídos (igual a bytesRead).</returns>
        /// 
        public int ReadResponse(byte[] buffer, out int bytesRead)
        {
            bytesRead = 0;

            // Determina el modo de lectura según el parámetro de configuración.
            string readMode = ChildParameters.ReadMode switch
            {
                0 => "standard",   // Leer hasta que no haya más datos o se cierre la conexión
                1 => "buffered",   // Leer un bloque fijo de bytes
                2 => "CRLF",       // Leer hasta encontrar la secuencia \r\n
                3 => "immediate",  // Leer solo lo que haya disponible en el buffer
                _ => "standard"
            };

            switch (readMode)
            {
                case "standard":
                    // Lee datos en un bucle hasta que no haya más datos disponibles o se cierre la conexión.
                    int totalRead = 0;
                    int read;
                    do
                    {
                        read = _stream.Read(buffer, totalRead, buffer.Length - totalRead);
                        totalRead += read;
                    } while (read > 0 && totalRead < buffer.Length && _stream.DataAvailable);
                    bytesRead = totalRead;
                    break;

                case "buffered":
                    // Lee un bloque fijo de bytes (del tamaño del buffer).
                    bytesRead = _stream.Read(buffer, 0, buffer.Length);
                    break;

                case "CRLF":
                    // Lee byte a byte hasta encontrar la secuencia \r\n (0x0D 0x0A) o llenar el buffer.
                    var temp = new List<byte>();
                    while (true)
                    {
                        int b = _stream.ReadByte();
                        if (b == -1) break; // Fin de stream
                        temp.Add((byte)b);
                        if (temp.Count >= 2 && temp[^2] == 0x0D && temp[^1] == 0x0A) // Detecta \r\n
                            break;
                        if (temp.Count >= buffer.Length) break; // Evita desbordar el buffer
                    }
                    temp.CopyTo(buffer);
                    bytesRead = temp.Count;
                    break;

                case "immediate":
                    // Espera brevemente a que haya datos disponibles en el buffer (máximo 200 ms).
                    int waitedMs = 0;
                    int maxWaitMs = 200;
                    while (!_stream.DataAvailable && waitedMs < maxWaitMs)
                    {
                        Thread.Sleep(5);
                        waitedMs += 5;
                    }
                    // Lee solo los datos que estén disponibles en ese momento.
                    if (_stream.DataAvailable)
                        bytesRead = _stream.Read(buffer, 0, buffer.Length);
                    else
                        bytesRead = 0;
                    break;
            }
            return bytesRead;
        }

        /// <summary>
        /// Cierra y libera los recursos utilizados por el servicio TCP.
        /// </summary>
        public void Dispose()
        {
            _stream?.Close();
            _stream?.Dispose();
            _client?.Close();
            _client?.Dispose();
        }
    }


}
