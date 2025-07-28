using AP.Data.Transfer.Controllers;
using AP.Data.Transfer.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TR.Torrey.Weight.Capture.Models;
using TR.Torrey.Weight.Capture.Repositories;
using TR.Torrey.Weight.Capture.Services;
using System.Net.Sockets;
using System.Net;
using System.Threading;
using System.Windows.Input;
using System.Collections.Concurrent;
using System.Windows.Forms;
using System.IO;
using System.Windows.Shapes;

namespace TR.Torrey.Weight.Capture.Viewmodels
{
    public class WeighingViewModel : BaseViewModel
    {
        IDialogAlert _dialogAlert;
        public ICommand CommunicationDisconectCommand { get; }
        public ICommand ReportCommand { get; }
        public ICommand ReconectarCommand { get; }

        private static ConcurrentDictionary<string, Socket> sockets = new ConcurrentDictionary<string, Socket>();
        private static ConcurrentDictionary<string, CancellationTokenSource> cancellationTokens = new ConcurrentDictionary<string, CancellationTokenSource>();
        private static ConcurrentDictionary<string, float> maximosPesos = new ConcurrentDictionary<string, float>();
        public Dictionary<string, int> dictionaryWeighings = new Dictionary<string, int>();
        public Dictionary<string, float> dictionaryTotalWeighings = new Dictionary<string, float>();
        public Dictionary<string, IPEndPoint> dictionaryScales = new Dictionary<string, IPEndPoint>();

        public static string pathFile = "datos_maximo.csv";

        public APSocketController socketController;

        public APSocket serverSocket { get; set; }

        public Task<List<Models.Scale>> allScales { get; set; }
        public ObservableCollection<Models.Scale>? _scales { get; set; }
        private ObservableCollection<Models.Scale> _filteredScales;
        public string _scalesFound { get; set; }
        public bool readScale = true;


        #region TEXT VALUES
        private int _onlineCount;
        public int OnlineCount
        {
            get => _onlineCount;
            set
            {
                _onlineCount = value;
                OnPropertyChanged(nameof(OnlineCount));
            }
        }

        private int _offlineCount;
        public int OfflineCount
        {
            get => _offlineCount;
            set
            {
                _offlineCount = value;
                OnPropertyChanged(nameof(OfflineCount));
            }
        }

        private string _filterText;
        public string FilterText
        {
            get => _filterText;
            set
            {
                _filterText = value;
                OnPropertyChanged(nameof(FilterText));
                ApplyFilter();
            }
        }
        public string ScalesFound
        {
            get => _scalesFound;
            set
            {
                _scalesFound = value;
                OnPropertyChanged(nameof(ScalesFound));
            }
        }
        public ObservableCollection<Models.Scale> Scales
        {
            get => _filteredScales;
            private set
            {
                _filteredScales = value;
                OnPropertyChanged(nameof(Scales));
            }
        }
        private string _weight;
        public string Weight
        {
            get => _weight;
            set
            {
                _weight = value;
                OnPropertyChanged(nameof(Weight));
            }
        }

        private string _txtTxtReport = Common.Common.MSG_CREATE_REPORT;
        public string TxtReport
        {
            get => _txtTxtReport;
            set
            {
                _txtTxtReport = value;
                OnPropertyChanged();
            }
        }
        #endregion

        public WeighingViewModel(IDialogAlert dialogAlert)
        {
            ReportCommand                   = new RelayCommand<int>(Report);
            CommunicationDisconectCommand   = new RelayCommand<int>(CommunicationDisconect);
            ReconectarCommand               = new RelayCommand<Scale>(ReconectarBascula);

            this._dialogAlert = dialogAlert;

            loadListScales();
            initFile();

            if (allScales?.Result.Count > 0)
                readScales();
            else
                ShowAlert(Common.Common.MSG_SCALE_NOT_FOUND);
        }


        public void loadListScales()
        {
            allScales = GetAll();
            _scales = new ObservableCollection<Models.Scale>(allScales.Result.ToList());

            var filter1 = _scales.Where(i => i.vIp != null && i.vName != null).ToList();
            _filteredScales = new ObservableCollection<Models.Scale>(filter1.Take(200));
            ScalesFound = MessageFound(_filteredScales.Count, allScales.Result.Count);

            FilterText = string.Empty;
        }
        private static async Task<List<Models.Scale>> GetAll()
        {
            var json = await ScaleRepository.scales();
            var response = JsonConvert.DeserializeObject<ResponseDataGeneric<List<Models.Scale>>>(json);

            if (response?.code == Common.Common.CODE_SUCCESS)
            {
                return response.data;
            }
            return new List<Models.Scale>();
        }
        private void ApplyFilter()
        {
            try
            {
                if (_scales == null) return;

                if (string.IsNullOrEmpty(_filterText))
                {
                    Scales = new ObservableCollection<Models.Scale>(_scales.Take(200));
                }
                else
                {
                    var filter1 = _scales.Where(i => i.vIp != null && i.vName != null).ToList();
                    var filtered = filter1.Where(i => i.vIp.Contains(_filterText) || i.vName.Contains(_filterText)).ToList();
                    Scales = new ObservableCollection<Models.Scale>(filtered);
                }

                ScalesFound = MessageFound(Scales.Count, allScales.Result.Count);
            }
            catch (Exception ex)
            {
                var message = ex.Message;
            }
        }

        #region REPORT
        public async void initFile()
        {
            try
            {
                if (File.Exists(pathFile))
                {
                    try
                    {
                        File.Delete(pathFile);
                    }
                    catch (Exception ex)
                    {
                    }
                }

                using (StreamWriter escritor = new StreamWriter(pathFile, true, Encoding.UTF8)) // Append to file
                {
                    escritor.WriteLine("" + Common.Common.MSG_SCALE + "," + Common.Common.MSG_IP + "," + Common.Common.MSG_WEIGHT + "," + Common.Common.MSG_DATE + "");
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error al guardar el peso máximo: {ex.Message}");
            }
        }
        public async void finishFile()
        {
            try
            {
                using (StreamWriter escritor = new StreamWriter(pathFile, true, Encoding.UTF8)) // Append to file
                {
                    escritor.WriteLine(",,");
                    escritor.WriteLine(",,");
                    escritor.WriteLine(",,");
                    escritor.WriteLine(",,");
                    escritor.WriteLine(",,");

                    escritor.WriteLine("" + Common.Common.MSG_SCALE + "," + Common.Common.MSG_IP + "," + Common.Common.MSG_SAMPLES + "," + Common.Common.MSG_TOTAL + "," + Common.Common.MSG_DATE + "");

                    foreach (var scale in dictionaryScales)
                    {
                        var scaleValues = _scales?.FirstOrDefault(b => b.vName == scale.Key);

                        var iSamples = dictionaryWeighings[scale.Key];
                        var fTotal = dictionaryTotalWeighings[scale.Key];

                        escritor.WriteLine(scale.Key + "," + scaleValues?.vIp + "," + iSamples + "," + fTotal + "," + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "");
                    }
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error al guardar el peso máximo: {ex.Message}");
            }
        }
        public void GuardaPesos(string nombreBascula, float pesoMaximo)
        {
            try
            {
                using (StreamWriter escritor = new StreamWriter(pathFile, true, Encoding.UTF8)) // Append to file
                {
                    var scaleValues = _scales?.FirstOrDefault(b => b.vName == nombreBascula);

                    float pesoMaximo_temp = (float)Math.Round(float.Parse(pesoMaximo.ToString()), 2);

                    escritor.WriteLine($"{nombreBascula},{scaleValues?.vIp},{pesoMaximo_temp},{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")}");
                }

            }
            catch (Exception ex)
            {
                ShowAlert(Common.Common.MSG_ERROR_SAVE_WEIGHT + " " + ex.Message);
            }
        }

        public void Report(int status)
        {
            CommunicationDisconect(0);
            resetScales();
            finishFile();

            SaveFileDialog saveDialog = new SaveFileDialog();
            saveDialog.InitialDirectory = @"C:\";
            saveDialog.Title = Common.Common.MSG_REPORT;
            saveDialog.Filter = "CSV files (*.csv)|*.csv|Excel Files|*.xls;*.xlsx";

            if (saveDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                var path = System.IO.Path.GetFullPath(saveDialog.FileName);

                System.Windows.Forms.Cursor.Current = System.Windows.Forms.Cursors.WaitCursor;

                try
                {
                    if (File.Exists(pathFile))
                    {
                        if (File.Exists(pathFile))
                            File.Copy(pathFile, path, true);
                        else
                            File.Copy(pathFile, path);
                    }

                    ShowAlert(Common.Common.MSG_REPORT_SAVED + path);

                    if (allScales?.Result.Count > 0)
                    {
                        readScale = true;
                        readScales();
                    }
                }
                catch (Exception ex)
                {
                    ShowAlert(Common.Common.MSG_ERROR_CREATE_REPORT + " " + ex.Message);
                }

                System.Windows.Forms.Cursor.Current = System.Windows.Forms.Cursors.Hand;
            }
        }

        #endregion
        public void readScales()
        {
            dictionaryScales.Clear();
            foreach (var sc in _scales.Take(200))
                dictionaryScales[sc.vName] = new IPEndPoint(IPAddress.Parse(sc.vIp), Common.Common.SCALE_PORT);

            readScale = true;

            foreach (var kvp in dictionaryScales)
            {
                var nombre = kvp.Key;
                var endpoint = kvp.Value;

                // Cancelar tarea anterior si existe
                if (cancellationTokens.TryRemove(nombre, out var oldToken))
                    oldToken.Cancel();

                var cts = new CancellationTokenSource();
                cancellationTokens[nombre] = cts;

                // Iniciar tarea de lectura recurrente
                Task.Run(async () =>
                {
                    while (readScale && !cts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            var peso = await LeerPesoDeBasculaAsync(nombre, endpoint);
                        }
                        catch (Exception ex)
                        {
                            // Omitimos errores por desconexión temporal
                        }

                        await Task.Delay(250, cts.Token); // 4 veces por segundo
                    }
                }, cts.Token);
            }

            // Tarea general para actualizar el estado online/offline de la UI 1 vez por segundo
            /*
            _ = Task.Run(async () =>
            {
                while (readScale)
                {
                    await Task.Delay(1000);
                    await App.Current.Dispatcher.InvokeAsync(() =>
                    {
                        OnlineCount = _scales.Count(s => s.iStatus == 1);
                        OfflineCount = _scales.Count(s => s.iStatus == 0);
                    });
                }
            });
            */
        }


        public void CommunicationDisconect(int status)
        {
            readScale = false;

            foreach (var token in cancellationTokens.Values)
            {
                try { token.Cancel(); } catch { }
            }
            cancellationTokens.Clear();

            foreach (var s in sockets.Values)
            {
                try { s.Shutdown(SocketShutdown.Both); s.Close(); } catch { }
            }
            sockets.Clear();
        }
        private void ReconectarBascula(Scale scale)
        {
            if (scale == null)
                return;

            scale.iStatus = 1; // Activar de nuevo la lectura
        }

        public void resetScales()
        {
            foreach (var scale in dictionaryScales)
            {
                var scaleValues = _scales?.FirstOrDefault(b => b.vName == scale.Key);
                scaleValues.iSamples = 0;
                scaleValues.fTotal = "0";
            }
        }

        public async Task<Dictionary<string, string>> LeerPesosDeBasculasAsync(Dictionary<string, IPEndPoint> basculas)
        {
            Dictionary<string, string> resultados = new Dictionary<string, string>();

            foreach (var bascula in basculas)
            {
                string nombreBascula = bascula.Key;
                IPEndPoint endpoint = bascula.Value;
                string peso = await LeerPesoDeBasculaAsync(nombreBascula, endpoint);

                resultados[nombreBascula] = peso;
            }

            return resultados;
        }
        public async Task<string> LeerPesoDeBasculaAsync(string nombreBascula, IPEndPoint endpoint)
        {
            string peso = string.Empty;
            var scale   = _scales?.FirstOrDefault(b => b.vName == nombreBascula);

            if (scale == null || scale.iStatus != 1)
                return peso;

            try
            {
                Socket socket;

                // Reutilizar socket existente o crear uno nuevo
                if (!sockets.TryGetValue(nombreBascula, out socket) || !socket.Connected)
                {
                    socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                    {
                        NoDelay = true
                    };

                    await socket.ConnectAsync(endpoint);
                    sockets[nombreBascula] = socket;
                }

                // Enviar comando para pedir peso
                byte[] comando = Encoding.ASCII.GetBytes("P");
                await socket.SendAsync(comando, SocketFlags.None);

                // Configurar cancelación por timeout usando Task.WhenAny
                var buffer = new byte[1024];
                Task<int> receiveTask = socket.ReceiveAsync(buffer, SocketFlags.None);
                Task timeoutTask = Task.Delay(1000);

                if (await Task.WhenAny(receiveTask, timeoutTask) == timeoutTask)
                {
                    throw new TimeoutException("Timeout al recibir peso.");
                }

                int bytesRead = receiveTask.Result;

                // Si no recibimos datos, consideramos que falló
                if (bytesRead == 0)
                {
                    scale.iStatus = 0;
                    return peso;
                }

                peso = Encoding.ASCII.GetString(buffer, 0, bytesRead).Trim('\r', '\n');

                string pesoNet = peso.TrimStart().Split(' ')[0];

                if (float.TryParse(pesoNet, out float pesoNumerico))
                {
                    float pesoRed = (float)Math.Round(pesoNumerico, 2);
                    float minWeight = (float)Math.Round(float.Parse(scale.fMinWeight), 2);

                    if (pesoRed <= minWeight &&
                        maximosPesos.TryGetValue(nombreBascula, out float pesoMaxAnt) &&
                        pesoMaxAnt > minWeight)
                    {
                        GuardaPesos(nombreBascula, pesoMaxAnt);
                        dictionaryWeighings[nombreBascula]++;
                        dictionaryTotalWeighings[nombreBascula] = (float)Math.Round(dictionaryTotalWeighings[nombreBascula] + pesoMaxAnt, 2);

                        scale.iSamples = dictionaryWeighings[nombreBascula];
                        scale.fTotal = dictionaryTotalWeighings[nombreBascula].ToString();
                        maximosPesos[nombreBascula] = 0;
                    }
                    else
                    {
                        maximosPesos.AddOrUpdate(nombreBascula, pesoRed, (key, oldVal) => Math.Max(oldVal, pesoRed));
                    }
                }

                // Reactivar la báscula si todo fue exitoso
                scale.iStatus = 1;
            }
            catch (Exception)
            {
                scale.iStatus = 0;

                // Intentar cerrar el socket en caso de error
                if (sockets.TryRemove(nombreBascula, out var socket))
                {
                    try { socket.Close(); } catch { }
                }
            }

            // Actualizar la UI desde el hilo principal
            await App.Current.Dispatcher.InvokeAsync(() =>
            {
                OnlineCount = _scales.Count(s => s.iStatus == 1);
                OfflineCount = _scales.Count(s => s.iStatus == 0);
            });

            return peso;
        }

        private string MessageFound(int scalesFound, int scales)
        {
            string message = Common.Common.MSG_SHOWING_ITEM + " " + scalesFound + " " + Common.Common.MSG_OF_ITEM + " " + scales;
            return message;
        }
        private void showScaleList()
        {
            this._dialogAlert.showScaleList();
        }
        private void ShowAlert(string message)
        {
            this._dialogAlert.showSimpleDialogAlert(message);
        }
    }
}