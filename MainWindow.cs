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
        public List<Variable> variableList { get; set; }

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

            if (txtVarExp.Text.Equals(string.Empty) == true)
            {
                MessageBox.Show(this, "Varexp.dat not defined and not loaded");
            }

            if(LblRes.BackColor.Equals(Color.Green) == false)
            {
                MessageBox.Show(this, "Varexp.dat not loaded or could not be loaded");
            }
            
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

            //Trendlist Trendlist = new Trendlist();

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
            string prev_TS = null;
            string prev_VN = null;
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
                        int dup = 0;
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
                                if (filetimeTimestamp.Equals(prev_TS) == true && currentVariable.Equals(prev_VN) == true)
                                {
                                    dup++;
                                    continue;
                                }
                                    var value = cells[1];
                                    var quality = 192;
                                    if (value == "?")
                                    {
                                        quality = 0;
                                        value = "0";
                                    }

                                //Trendlist.Add(new Entry(filetimeTimestamp, currentVariable, value, quality.ToString()));
                                //Trendlist.truncateDuplicates(Trendlist.Last<Entry>());
                                    int entrylengthTrend = 4+Int32.Parse(txtExtColTrends.Text);
                                    String[] Trendentry = new String[entrylengthTrend];
                                    
                                    Trendentry[0] = filetimeTimestamp;
                                    Trendentry[1] = currentVariable;
                                    Trendentry[2] = value;
                                    Trendentry[3] = quality.ToString();
                                    for (int i = 4; i < entrylengthTrend; i++)
                                    {
                                        Trendentry[i] = "";
                                    }

                                    writerTrends.WriteLine(string.Join(",", Trendentry));

                                        prev_TS = filetimeTimestamp;
                                        prev_VN = currentVariable;
                            }
                            else if (currentBlock == BlockMode.Logs)
                            {
                                //Chrono, Loglist, AssocLabel, EvtNumber, EvtTitle, Name, Value, ValueT, Quality, AlarmLevel, AlarmState, UserComment, Threshold, NumParam, TextParam
                                var evtNumber = assignHDSEvtNb(cells[2]);
                                var evtTitle = string.Empty;
                                var variableName = cells[5];
                                var assocLabel = string.Empty;
                                var rawValue = cells[7];
                                double value;
                                var valueT = string.Empty;
                                var quality = 192;
                                var alarmLevel = assignAlarmPrio(variableName);
                                var alarmState = cells[8];
                                var threshold = 0;
                                var comment = cells[11].Replace(" ",String.Empty);
                                var numParam = 0;
                                var textParam = string.Empty;

                                if (!double.TryParse(rawValue, out value))
                                {
                                    valueT = rawValue;
                                }


                                if (cells[9].Equals("") == false)
                                {
                                    filetimeTimestamp = ToFiletimeTimestamp(cells[9]);
                                }

                                int entrylengthLog = 15 + Int32.Parse(txtExtColLogs.Text);
                                String[] Logentry = new String[entrylengthLog];

                                Logentry[0] = filetimeTimestamp;
                                Logentry[1] = currentLoglist;
                                Logentry[2] = assocLabel;
                                Logentry[3] = evtNumber;
                                Logentry[4] = evtTitle;
                                Logentry[5] = variableName;
                                Logentry[6] = value.ToString();
                                Logentry[7] = valueT;
                                Logentry[8] = quality.ToString();
                                Logentry[9] = alarmLevel;
                                Logentry[10] = alarmState.ToString();
                                Logentry[11] = comment;
                                Logentry[12] = threshold.ToString();
                                Logentry[13] = numParam.ToString();
                                Logentry[14] = textParam;

                                for (int i = 15; i < entrylengthLog; i++)
                                {
                                    Logentry[i] = "";
                                }

                                writerLogs.WriteLine(string.Join(",",Logentry));
                            }
                        }
                    }

                    //Trendlist.Clear();
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

        private string assignAlarmPrio(string variableName)
        {
            string alarmLevel = String.Empty;


            foreach (Variable var in this.variableList)
            {
                if (var.name.Equals(variableName) == true)
                {
                    alarmLevel = var.level;
                }
            }

            return alarmLevel;
        }


        private string assignHDSEvtNb(string evtNbProp )
        {
            string evtNbHDS = String.Empty;
            long evtNBIn = Int64.Parse(evtNbProp);
            long evtNBOut = 0;

                switch (evtNBIn)
                {
                    case 0:
                        //No Event
                        evtNBOut = 0;
                        break;
                    case 1:
                        //Command to 0 obsolete
                        evtNBOut = 2048;
                        break;
                    case 2:
                        //Command to 1 obsolete 
                        evtNBOut = 4096;
                        break;
                    case 4:
                        //Command to 1 obsolete
                        evtNBOut = 4096;
                        break;
                    case 8:
                        //Send Register
                        evtNBOut = 262144;
                        break;
                    case 16:
                        //Send Text
                        evtNBOut = 524288;
                        break;
                    case 32:
                        //Send Recipe
                        evtNBOut = 536870912;
                        break;
                    case 64:
                        //Bit Transition to 0
                        evtNBOut = 512;
                        break;
                    case 128:
                        //Start-up project
                        evtNBOut = 16777216;
                        break;
                    case 256:
                        //Close-down project
                        evtNBOut = 33554432;
                        break;
                    case 512:
                        //Bit Transition to 1
                        evtNBOut = 1024;
                        break;
                    case 1024:
                        //Bit Transition to NS
                        evtNBOut = 6144;
                        break;
                    case 2048:
                        //Alarm On, acknowledged
                        evtNBOut = 2;
                        break;
                    case 4096:
                        //Alarm On, not acknowledged
                        evtNBOut = 1;
                        break;
                    case 8192:
                        //Alarm Off, acknowledged
                        evtNBOut = 8;
                        break;
                    case 16384:
                        //Alarm Off, not acknowledged
                        evtNBOut = 4;
                        break;
                    case 32768:
                        //Alarm NS
                        evtNBOut = 16;
                        break;
                    case 65536:
                        //Alarm acknowledged by operator
                        evtNBOut = 2097152;
                        break;
                    case 131072:
                        //Forced by program
                        evtNBOut = 1048576;
                        break;
                    case 262144:
                        //Alarm to Off
                        evtNBOut = 80589934593;
                        break;
                    case 524288:
                        //Alarm to On
                        evtNBOut = 4294967296;
                        break;
                    case 1048576:
                        //Alarm not accessible
                        evtNBOut = 4194304;
                        break;
                    case 2097152:
                        //Alarm inhibited
                        evtNBOut = 8388608;
                        break;
                    case 4194304:
                        //Alarm masked by program
                        evtNBOut = 32768;
                        break;
                    case 8388608:
                        //Alarm masked by variable
                        evtNBOut = 65536;
                        break;
                    case 16777216:
                        //Alarm masked by operator
                        evtNBOut = 64;
                        break;
                    case 33554432:
                        //Alarm masked by expression
                        evtNBOut = 131072;
                        break;
                    case 67108864:
                        //Operator action to mask an alarm
                        evtNBOut = 13421728;
                        break;
                    case 134217728:
                        //Operator Action to unmask an alarm
                        evtNBOut = 268435456;
                        break;
                    case 268435456:
                        //Begin maintenance mode
                        evtNBOut = 1073741824;
                        break;
                    case 536870912:
                        //End maintenance mode
                        evtNBOut = 2147483648;
                        break;
                    case 1073741824:
                        //Attempt to log in
                        evtNBOut = 67108864;
                        break;
                    case 4294967296:
                        //Command to 0
                        evtNBOut = 2048;
                        break;
                    case 8589934592:
                        //Command to 1
                        evtNBOut = 4096;
                        break;
                    case 17179869184:
                        //Command to 1 - Status has 0
                        evtNBOut = 512;
                        break;
                    case 34359738368:
                        //Command to 1 - Status has 1
                        evtNBOut = 1024;
                        break;
                }
                evtNbHDS = evtNBOut.ToString();
            return evtNbHDS;
        }

        static string ToFiletimeTimestamp(string timestamp)
        {
            const string format = "yyyyMMddTHHmmss.fffZ";
            var datetime = DateTime.ParseExact(timestamp, format, CultureInfo.InvariantCulture, DateTimeStyles.None);
            return datetime.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture);
        }

        private void button1_Click(object sender, EventArgs e)
        {
            var dlgVarExp = new OpenFileDialog();

            if (dlgVarExp.ShowDialog(this) == DialogResult.OK)
            {
                txtVarExp.Text = dlgVarExp.FileName;
            }
        }

        private Result initVariableDataBase()
        {
            this.variableList = new List<Variable>();
            string varname = null;
            string level = null;

            if(txtVarExp.Text.Equals(String.Empty)==false)
            {
            foreach (string line in File.ReadAllLines(txtVarExp.Text, Encoding.GetEncoding(850)))
            {
                if (line.StartsWith("ALA") || line.StartsWith("ACM") || line.StartsWith("ATS"))
                {

                    string[] split = line.Split(new char[]{','}, 52);
                        if (line != null)
                            {
                                 varname = "";
                                for (int i = 2; i <= (13); i++)
                                {
                                    varname += split[i];
                                    if (split[i + 1].Equals("") == false)
                                    {
                                        varname += ".";
                                    }
                                }
                                level = split[50];
                            }


                    Variable var = new Variable(varname,level);
                    variableList.Add(var);


                }
            }
                        if (variableList == null) return Result.Failed;
                        else return Result.Ok;
            }
            else 
            {
                return Result.Failed;
            }
        }

        private void BtnLoadVarExp_Click(object sender, EventArgs e)
        {
            Result res = (Result) this.initVariableDataBase();

            if (res == Result.Ok)
            {
                LblRes.BackColor = Color.Green;
            }
            else 
            {
                LblRes.BackColor = Color.Red;
            }

        }
    }
    public class Variable
    {
        public string name { get; set; }
        public string level { get; set; }

     //Constructor for full creation without boolean properties
     public Variable(string name, string level)
        {
         this.name = name;
         this.level = level;
        }
    }

    public class Entry
    { 
     //Chrono, Name, Value, Quality
        private string chrono;
        private string name;
        private string value;
        private string quality;

        public Entry(string chrono, string name, string value, string quality)
        {
            this.chrono = chrono;
            this.name = name;
            this.value = value;
            this.quality = quality;
        }

        public string Chrono
        {
            get { return chrono; }
            set { chrono = value; }
        }

        public string Name
        {
            get { return name; }
            set { name = value; }
        }
       
        public string Value
        {
            get { return this.value; }
            set { this.value = value; }
        }
        

        public string Quality
        {
            get { return quality; }
            set { quality = value; }
        }
    }

    public class Trendlist : List<Entry>
    { 
    
        public void truncateDuplicates(Entry entry)
        {
            bool found = false;
            foreach (Entry s_entry in this)
            { 
                if(s_entry.Equals(entry)) found = true;
            }

            if (found) this.Remove(entry);
        }
    }
    public enum Result
    {
        Ok,
        Failed
    }
}
