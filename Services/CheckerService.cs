using System;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using TestPlan.Entities;
using TestPlan.Entities.Enumeraciones;
using TestPlan.Entities.Views;
using TestPlan.Logic;
using TestPlan.Logic.Models;

/// <summary>
/// Servicio para comprobar y obtener datos actualizados de la bd cada segundo
/// </summary>
public class CheckerService
{
    /// <summary>
    /// token de cancelación
    /// </summary>
    private CancellationTokenSource _cts = new CancellationTokenSource();
    /// <summary>
    /// Intervalo de comprobación
    /// </summary>
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(1);
    /// <summary>
    /// Indica si el servicio esta activo
    /// </summary>
    private bool _isEnabled = false;

    /// <summary>
    /// Contando segundos
    /// </summary>
    private int _counter = 0;

    /// <summary>
    /// Lenguaje actual de la aplicación
    /// </summary>
    private eLanguage _currentLanguage = 0;

    /// <summary>
    /// Evento para notificar cuando cambia el idioma de la aplicación.
    /// </summary>
    public event Action<CultureInfo>? LanguageChanged;

    /// <summary>
    /// Inicia el servicio
    /// </summary>
    public void Start()
    {
        _cts = new CancellationTokenSource();
        if (!_isEnabled)
        {
            _isEnabled = true;
            //Ejemplo para controlar excepciones en tareas, usar await, si no usas await para comprobarlo debes usar el continueWith y el IsFaulted, 
            try
            {
                Task.Run(async () =>
                           {
                               while (_isEnabled && !_cts.IsCancellationRequested)
                               {
                                   Log.Information("Checking cycle mode...");
                                   CheckCycleMode();
                                   if (_counter % 3 == 0)
                                   {
                                       Log.Information("Checking Current Configuration ...");
                                       CheckCurrentConfiguration();
                                       _counter = 0;
                                   }
                                   _counter++;
                            
                                   await Task.Delay(_interval, _cts.Token);
                               }
                           }).ContinueWith((task) =>
                           {
                               if (task.IsFaulted)
                               {
                                   Log.Error("CheckerService errors: {mensaje}", task.Exception?.InnerException?.Message);
                               }
                           }
                           );
         
            }
            catch (TaskCanceledException ex)
            {
                //Para capturar en caso de utilizar el await, pero queremos que ruede la tarea  no que espere
                Log.Error(ex, "TaskCanceledException");
            }
            Log.Debug("CheckerService Start");
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

    /// <summary>
    /// Comprueba el modo del ciclo, obtiene los datos de la BD
    /// </summary>
    /// <returns></returns>
    private void CheckCycleMode()
    {
        using (DefinitionCyclemodeLogic logic = new DefinitionCyclemodeLogic())
        {
            CycleModeView cycleModeView = logic.GetCyclemodesData();
            //Actualizar el modo del ciclo
            Config.Instance.UpdateCycleModeData(cycleModeView);
        }
    }
    /// <summary>
    /// Comprueba la configuración general actual. Si cambia el idioma de la aplicación en la base de datos,
    /// llama al evento LanguageChanged para actualizar la cultura de la aplicación.
    /// </summary>
    private void CheckCurrentConfiguration()
    {
        eLanguage newIdioma = 0;
        //Obtener la configuración general
        using (ConfigGeneralLogic configLo = new ConfigGeneralLogic())
        {
            GetConfigGeneral? config = configLo.GetCurrentConfiguration(1);
            if (config == null)
            {
                Log.Error("Unable to load current configuration.");
                //ErrA00026
                return;
            }
            if(Enum.IsDefined(typeof(eLanguage), config.Idioma))
            {
                newIdioma = (eLanguage)config.Idioma;
                if (_currentLanguage != newIdioma) { OnLanguageChanged(newIdioma); }
            }
            else
            {
                Log.Error("Language: {Language} is undefined in eLanguage.", config.Idioma);
            }
            Config.Instance.UpdateConfig(config);
        }
    }

    /// <summary>
    /// Método que lanza el evento para actualizar la cultura de la aplicación cuando cambia el idioma.
    /// </summary>
    /// <param name="lenguaje"></param>
    private void OnLanguageChanged(eLanguage lenguaje)
    {
        _currentLanguage = lenguaje;
        CultureInfo culture;

        switch (_currentLanguage)
        {
            case eLanguage.English:
                culture = new CultureInfo("en-US");
                break;
            case eLanguage.Spanish:
                culture = new CultureInfo("es-ES");
                break;
            case eLanguage.German:
                culture = new CultureInfo("de-DE");
                break;
            case eLanguage.Italian:
                culture = new CultureInfo("it-IT");
                break;
            case eLanguage.Chinese:
                culture = new CultureInfo("zh-CN");
                break;
            case eLanguage.Slovenian:
                culture = new CultureInfo("sl-SI");
                break;
            case eLanguage.Turkish:
                culture = new CultureInfo("tr-TR");
                break;
            case eLanguage.Russian:
                culture = new CultureInfo("ru-RU");
                break;
            case eLanguage.French:
                culture = new CultureInfo("fr-FR");
                break;
            case eLanguage.Polish:
                culture = new CultureInfo("pl-PL");
                break;
            default:
                Log.Error("Language: {idioma} is not defined in eLanguage.", _currentLanguage.ToString());
                return;
           
        }
        Log.Information("Switching to language: {idioma}", _currentLanguage.ToString());
        LanguageChanged?.Invoke(culture);

    }

}