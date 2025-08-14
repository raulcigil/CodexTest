using Serilog;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AsyncLock = TestPlan.Logic.Services.Helpers.AsyncLock;


namespace TestPlan.Logic.Services.TCP
{
    /// <summary>
    /// Clase singleton para gestionar la conexión TCP.
    /// </summary>
    public class TCPConnectionManager
    {
        #region Singleton Implementation
        private static readonly object _lock = new object();
        private static TCPConnectionManager _instance;
        private TcpClient _client;
        private NetworkStream _stream;
        public event Action<string> DataReceived;
        private bool _listeningStarted = false;
        private readonly AsyncLock _lockAsync = new AsyncLock();
        #endregion

        #region Private Constructor
        /// <summary>
        /// instance of the TCPConnectionManager.
        /// </summary>
        public static TCPConnectionManager Instance
        {
            get
            {
                lock (_lock)
                {
                    return _instance ??= new TCPConnectionManager();
                }
            }
        }
        #endregion

        #region Connected property
        /// <summary>
        /// Gets a value indicating whether the client is currently connected.
        /// </summary>
        public bool IsConnected => _client?.Connected ?? false;
        #endregion

        #region Method Connection
        /// <summary>
        /// Establishes a TCP connection to the specified IP address and port.
        /// </summary>
        /// <remarks>If a connection is already active, this method reuses the existing connection and
        /// returns <see langword="true"/>. If the connection is successfully established for the first time, a
        /// listening loop is started to handle incoming data. In case of a failure, the method logs the error and
        /// resets the internal connection state.</remarks>
        /// <param name="ip">The IP address of the remote host to connect to. Must be a valid IPv4 address.</param>
        /// <param name="port">The port number of the remote host to connect to. Must be in the range 0 to 65535.</param>
        /// <returns><see langword="true"/> if the connection is successfully established or if a connection is already active;
        /// otherwise, <see langword="false"/> if the connection attempt fails.</returns>
        public async Task<bool> StartConnectionAsync(string ip, int port, CancellationToken cancellationToken)
        {
            using var ctsLock = await _lockAsync.LockAsync(cancellationToken); // usamos un lock async

            if (IsConnected)
            {
                Log.Information("TCP already connected, reusing existing connection.");
                return true;
            }

            try
            {
                Log.Information($"Starting TCP connection to {ip}:{port}");

                _client = new TcpClient(AddressFamily.InterNetwork);

                // Inicia conexión con timeout y cancelación
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var connectTask = _client.ConnectAsync(IPAddress.Parse(ip), port);

                var completedTask = await Task.WhenAny(connectTask, Task.Delay(Timeout.Infinite, connectCts.Token));

                if (completedTask != connectTask)
                {
                    Log.Warning("TCP connection attempt timed out or was cancelled.");
                    _client.Dispose();
                    _client = null;
                    return false;
                }

                _stream = _client.GetStream();
                Log.Information($"TCP connection established to {ip}:{port}");

                if (!_listeningStarted)
                {
                    _listeningStarted = true;
                    StartListeningLoop();
                }

                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error establishing TCP communication");
                _client = null;
                _stream = null;
                return false;
            }
        }
        #endregion  

        /// <summary>
        /// Retrieves the underlying network stream associated with the current connection.
        /// </summary>
        /// <remarks>The returned <see cref="NetworkStream"/> provides access to the data stream for the
        /// connection.  Ensure that the stream is properly disposed of when no longer needed to release
        /// resources.</remarks>
        /// <returns>A <see cref="NetworkStream"/> object that represents the network stream for sending and receiving data.</returns>
        public NetworkStream GetStream()
        {
            return _stream;
        }
        /// <summary>
        /// Reads data from the underlying stream into the specified buffer.
        /// </summary>
        /// <remarks>This method blocks until data is available to read or the connection is closed.  If
        /// the stream is not readable or an error occurs, the method logs the error and returns 0.</remarks>
        /// <param name="buffer">A byte array that serves as the destination for the data read from the stream.  The array must be large
        /// enough to hold the data being read.</param>
        /// <param name="length">When this method returns, contains the number of bytes successfully read into the buffer.  This value is set
        /// to 0 if no data is read or if an error occurs.</param>
        /// <returns>The number of bytes read from the stream. Returns 0 if the stream is not readable,  if the connection is
        /// closed, or if an error occurs during the read operation.</returns>
        public int ReadResponse(byte[] buffer, out int length)
        {
            length = 0;

            var stream = GetStream();
            if (stream == null || !stream.CanRead)
                return 0;

            try
            {
                // Esta llamada se queda bloqueada hasta que lleguen datos o se cierre la conexión
                int bytesRead = stream.Read(buffer, 0, buffer.Length);

                if (bytesRead > 0)
                {
                    length = bytesRead;
                }

                return bytesRead;
            }
            catch (IOException ioEx)
            {
                Log.Error(ioEx, "Stream closed or read interrupted");
                return 0;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Unexpected error in TCP read");
                return 0;
            }
        }
        /// <summary>
        /// 
        /// </summary>
        public void StartListeningLoop()
        {
            Task.Run(() =>
            {
                try
                {
                    Log.Information("[BARCODE READER] Iniciando bucle de lectura del stream...");

                    while (true)
                    {
                        byte[] buffer = new byte[1024];
                        int bytesRead = ReadResponse(buffer, out int len);

                        if (bytesRead <= 0)
                        {
                            Log.Warning("[BARCODE READER] Fin del stream o error de lectura. Cerrando bucle.");
                            break;
                        }

                        string data = Encoding.ASCII.GetString(buffer, 0, len).Trim();

                        if (string.IsNullOrEmpty(data) || data.Length < 2)
                        {
                            Log.Warning("[BARCODE READER] Dato vacío o inválido recibido.");
                            continue;
                        }
                        //TODO: RAUL ¿PREFIJO DE TIPO? PREGUNTAR A GABI
                        data = data.Substring(1); // Quitamos el prefijo de tipo

                        Log.Information($"[BARCODE READER] Leemos datos del stream: {data}");

                        DataReceived?.Invoke(data);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[BARCODE READER] Error inesperado en bucle de lectura");
                }
            });
        }
        /// <summary>
        /// Closes the TCP connection and releases associated resources.
        /// </summary>
        /// <remarks>This method ensures that the connection is properly closed and any associated
        /// resources,  such as the network stream, are released. It is thread-safe and can be called multiple times 
        /// without throwing exceptions. If the connection is already closed, the method has no effect.</remarks>
        public void Close()
        {
            lock (_lock)
            {
                if (_client != null)
                {
                    Log.Information("Closing TCP connection.");
                    _stream?.Close();
                    _client.Close();
                    _stream = null;
                    _client = null;
                }
            }
        }
    }

}
