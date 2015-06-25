using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Prop2HdsConverter
{
    public partial class MainWindow : Form
    {
        enum BlockMode
        {
            Trends,
            Logs
        }

        public class ConvertSingleFileParameters
        {
            public FileSystemInfo File { get; set; }
            public ManualResetEvent IsConversionCompletedEvent { get; set; }
            public bool IsConversionCompleted { get; set; }

            public ConvertSingleFileParameters(FileSystemInfo fileInfo, ManualResetEvent isCompletedEvent)
            {
                File = fileInfo;
                IsConversionCompletedEvent = isCompletedEvent;
            }
        }

        private List<ConvertSingleFileParameters> _convertSingleFileParameters;
        private BackgroundWorker _conversionWorker = new BackgroundWorker();
        private delegate void OnTraceOutput(string message, params object[] parameter);
        private delegate void OnUpdateProgressBar ();

        public MainWindow()
        {
            InitializeComponent();

            _conversionWorker.WorkerReportsProgress = true;
            _conversionWorker.WorkerSupportsCancellation = true;
            _conversionWorker.DoWork += DoConvert;
            _conversionWorker.RunWorkerCompleted += ConversionCompleted;
        }
        
        private void btnSelectSourceFolder_Click (object sender, EventArgs e)
        {
            var dlgSourceFolder = new FolderBrowserDialog();
            
            if (dlgSourceFolder.ShowDialog(this) == DialogResult.OK)
            {
                txtSourceFolder.Text = dlgSourceFolder.SelectedPath;
            }
        }

        private void btnSelectTargetFolder_Click (object sender, EventArgs e)
        {
            var dlgTargetFolder = new FolderBrowserDialog();
            dlgTargetFolder.ShowNewFolderButton = true;

            if (dlgTargetFolder.ShowDialog(this) == DialogResult.OK)
            {
                txtTargetFolder.Text = dlgTargetFolder.SelectedPath;
            }
        }

        void TraceOutput (string message, params object[] parameter)
        {
            if (InvokeRequired)
            {
                Invoke((OnTraceOutput)TraceOutput, message, parameter);
            }
            else
            {
                txtOutput.AppendText(string.Format(message, parameter) + Environment.NewLine);
            }
        }

        void UpdateProgressBar()
        {
            if (_convertSingleFileParameters == null)
            {
                return;
            }

            if (InvokeRequired)
            {
                Invoke((OnUpdateProgressBar) UpdateProgressBar);
            }
            else
            {
                pgProgress.Value = _convertSingleFileParameters.Count(p => p.IsConversionCompleted) * 100 /
                                   _convertSingleFileParameters.Count();
            }
        }

        void ConversionCompleted (object sender, RunWorkerCompletedEventArgs e)
        {
            TraceOutput("Conversion completed");
        }

        private void btnConvert_Click (object sender, EventArgs e)
        {
            if (!Directory.Exists(txtSourceFolder.Text))
            {
                MessageBox.Show(this, "Source folder does not exist.");
            }

            if (!Directory.Exists(txtTargetFolder.Text))
            {
                MessageBox.Show(this, "Target folder does not exist.");
            }

            if (Directory.GetFiles(txtTargetFolder.Text).Any())
            {
                if (MessageBox.Show(this,
                                    "Target directory is not empty. Its content will automatically be overwritten. Do you really want to continue?",
                                    "Warning", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1) == DialogResult.No)
                {
                    return;
                }
            }

            txtOutput.Text = string.Empty;
            _conversionWorker.RunWorkerAsync();
        }

        private void btnCancel_Click (object sender, EventArgs e)
        {
            TraceOutput("Cancellation requested - Trying to stop worker threads...");
            _conversionWorker.CancelAsync();
        }

        void DoConvert(object sender, DoWorkEventArgs e)
        {                       
            TraceOutput("Starting conversion.");

            var fiSource = new DirectoryInfo(txtSourceFolder.Text);
            var datFiles = fiSource.EnumerateFiles("*.dat");

            TraceOutput("{0} files found for conversion.", datFiles.Count());

            var isConversionCompletedEvents = new ManualResetEvent[datFiles.Count()];
            _convertSingleFileParameters = new List<ConvertSingleFileParameters>();

            var i = 0;
            foreach (var file in datFiles)
            {
                isConversionCompletedEvents[i] = new ManualResetEvent(false);

                var convertSingleFileParameters = new ConvertSingleFileParameters(file, isConversionCompletedEvents[i]);
                _convertSingleFileParameters.Add(convertSingleFileParameters);
                
                ThreadPool.QueueUserWorkItem(ConvertSingleFile, convertSingleFileParameters);
                i++;
            }
            
            WaitHandle.WaitAll(isConversionCompletedEvents);            
        }

        FileStream CreateFile(string filename)
        {
            FileStream file = null;
            try
            {
                 file = File.Create(filename);
                 file.Close();
            }
            catch (Exception ex)
            {
                TraceOutput("ERROR on creating file: " + ex.Message + " Skipping file: " + filename);
            }

            return file;
        }


        void ConvertSingleFile(object convertSingleFileParameters)
        {
            var parameters = (ConvertSingleFileParameters) convertSingleFileParameters;
            var file = parameters.File;
            var isCompleted = parameters.IsConversionCompletedEvent;

            var csvFileTrends = CreateFile(txtTargetFolder.Text + "\\" + file.Name + "_Trends.csv");
            if (csvFileTrends == null)
            {
                isCompleted.Set();
                parameters.IsConversionCompleted = true;
                return;
            }

            var csvFileLogs = CreateFile(txtTargetFolder.Text + "\\" + file.Name + "_Logs.csv");
            if (csvFileLogs == null)
            {
                isCompleted.Set();
                parameters.IsConversionCompleted = true;
                return;
            }

            var writerTrends = new StreamWriter(csvFileTrends.Name);
            var writerLogs = new StreamWriter(csvFileLogs.Name);

            string currentVariable = null;
            string currentLoglist = null;
            BlockMode? currentBlock = null;
            var lineNumber = 0;

            try
            {
                using (var reader = new StreamReader(file.FullName))
                {
                    TraceOutput("Processing file {0}.", file.Name);

                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        lineNumber++;

                        string[] cells;
                        //TraceOutput("Processing line {0}.", lineNumber++);
                        if (line.Trim().StartsWith("LB"))
                        {
                            cells = Regex.Split(line, ",");
                            if (cells[3].Trim() == "L")
                            {
                                currentBlock = BlockMode.Logs;
                                currentLoglist = cells[4].Trim();
                            }

                            if (cells[3].Trim() == "T")
                            {
                                currentBlock = BlockMode.Trends;
                            }
                            continue;
                        }

                        if (line.Trim().StartsWith("TR"))
                        {
                            currentVariable = Regex.Split(line, ",")[1];
                            continue;
                        }

                        if (line.Trim().StartsWith("LC"))
                        {
                            continue;
                        }

                        cells = Regex.Split(line, ",");
                        var filetimeTimestamp = ToFiletimeTimestamp(cells[0]);

                        if (currentBlock.HasValue)
                        {
                            if (currentBlock == BlockMode.Trends)
                            {
                                if (currentVariable == null)
                                {
                                    TraceOutput(
                                        "File format error. Variable needs to be declared before checking for values. File: {0}. Line: {1}. Skipping file.",
                                        file.Name, lineNumber);
                                    break;
                                }

                                //Chrono, Name, Value, Quality
                                var value = cells[1];

                                var quality = 192;
                                if (value == "?")
                                {
                                    quality = 0;
                                }

                                writerTrends.WriteLine("{0},{1},{2},{3}", filetimeTimestamp, currentVariable, value, quality);
                            }
                            else if (currentBlock == BlockMode.Logs)
                            {
                                //Chrono, Loglist, AssocLabel, EvtNumber, EvtTitle, Name, Value, ValueT, Quality, AlarmLevel, AlarmState, UserComment, Threshold, NumParam, TextParam
                                var evtNumber = cells[2];
                                var evtTitle = string.Empty;
                                var variableName = cells[5];
                                var assocLabel = string.Empty;
                                var rawValue = cells[7];
                                double value;
                                var valueT = string.Empty;
                                var quality = 192;
                                var alarmLevel = 1;
                                var alarmState = cells[8];
                                var threshold = 0;
                                var comment = cells[11];
                                var numParam = 0;
                                var textParam = string.Empty;

                                if (!double.TryParse(rawValue, out value))
                                {
                                    valueT = rawValue;
                                }

                                writerLogs.WriteLine(
                                    "{0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12},{13},{14}",
                                    filetimeTimestamp, currentLoglist, assocLabel, evtNumber, evtTitle, variableName,
                                    value, valueT, quality, alarmLevel, alarmState, comment, threshold, numParam,
                                    textParam);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                TraceOutput("ERROR on creating file: " + ex.Message + " Skipping file: " + file.Name);
            }
            finally
            {
                writerTrends.Close();
                writerLogs.Close();
            }

            isCompleted.Set();
            parameters.IsConversionCompleted = true;
            
            UpdateProgressBar();
        }


        static string ToFiletimeTimestamp(string timestamp)
        {
            const string format = "yyyyMMddTHHmmss.fffZ";
            var datetime = DateTime.ParseExact(timestamp, format, CultureInfo.InvariantCulture, DateTimeStyles.None);
            return datetime.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture);
        }
    }
}
