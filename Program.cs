using MySql.Data;
using MySql.Data.MySqlClient;
using Serilog;
using Serilog.Debugging;
using Serilog.Sinks.SystemConsole;
using System;
using System.Net;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Linq;

namespace LinkCheck
{
    internal class Program
    {
        static readonly HttpClient client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(3),
        };

        static void Main(string[] args)
        {
            Console.WriteLine("");
            Console.WriteLine("Homeschool Connections Link Health Check for Moodle");
            if (args.Length == 0) Console.WriteLine("Use -v for Verbose Mode, Press CTRL+C to Cancel");
            Console.WriteLine("");
            setupLogger(args);

            #if DEBUG
                Console.WriteLine("DEBUG Mode Build /w predefined database values");
                string DatabaseServer = "localhost";
                string DatabaseName = "hsc_moodle";
                string DatabaseUsername = "root";
                string DatabasePassword = "";
            #else
                // Console.WriteLine("RELEASE Mode Build");
                Console.Write("MySQL Database Address: ");
                string? DatabaseServer = Console.ReadLine();
                Console.Write("MySQL Database Name: ");
                string? DatabaseName = Console.ReadLine();
                Console.Write("MySQL Database Username: ");
                string? DatabaseUsername = Console.ReadLine();
                Console.Write("MySQL Database Password: ");
                string? DatabasePassword = Console.ReadLine();
                Console.WriteLine("");
            #endif

            DBConnection db = DBConnection.Instance();

            db.Server = DatabaseServer;
            db.DatabaseName = DatabaseName;
            db.Password = DatabasePassword;
            db.UserName = DatabaseUsername;

            try
            {
                db.IsConnect();
            }
            catch (Exception e)
            {
                Log.Logger.Fatal(e.ToString());
                Environment.Exit(-1);
            }

            string query = "SELECT id, course, name, externalurl FROM mdl_url";
            MySqlCommand cmd = new MySqlCommand(query, db.Connection);
            MySqlDataReader reader = cmd.ExecuteReader();
            var tasks = new List<MoodleURLEntry>();

            while (reader.Read())
            {
                int resourceID = reader.GetInt32(0);
                int resourceCourseID = reader.GetInt32(1);
                string resourceName = reader.GetString(2);
                string resourceURL = reader.GetString(3);

                MoodleURLEntry newEntry;

                try
                {
                    newEntry = new MoodleURLEntry(
                        rID: resourceID,
                        courseID: resourceCourseID,
                        name: resourceName,
                        url: resourceURL);
                    tasks.Add(newEntry);
                }
                catch (NullReferenceException e)
                {
                    Log.Logger.Warning(e.ToString());
                }
            }

            // await Task.WhenAll(tasks.Select(t => TestUrl(t)));

            int total = tasks.Count();
            int current = 0;

            Parallel.ForEach(tasks, task =>
            {
                try
                {
                    checkURL(task, current, total);
                }
                finally
                {
                    current++;
                }
            });

            Log.Logger.Information("Closing database connection");
            db.Close();
            Console.WriteLine("Job finished - Goodbye");
        }

        static void setupLogger(string[] args)
        {
#if DEBUG
            Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.Information()
                    .WriteTo.Console()
                    .WriteTo.File("log.txt", rollingInterval: RollingInterval.Day)
                    .CreateLogger();
#else
            if (args.Length > 0)
            {
                if (args[0] == "-v")
                {
                    Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.Information()
                    .WriteTo.Console()
                    .WriteTo.File("log.txt", rollingInterval: RollingInterval.Day)
                    .CreateLogger();
                }
                else
                {
                    Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.Warning()
                    .WriteTo.Console()
                    .WriteTo.File("log.txt", rollingInterval: RollingInterval.Day)
                    .CreateLogger();
                }
            }
            else
            {
                Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Warning()
                .WriteTo.Console()
                .WriteTo.File("log.txt", rollingInterval: RollingInterval.Day)
                .CreateLogger();
            }
#endif
        }

        static void checkURL(MoodleURLEntry entry, int current, int total)
        {
            bool invalidUrl = false;
            Uri uri;

            try
            {
                uri = new Uri(entry.URL);
            }
            catch (UriFormatException)
            {
                Log.Logger.Information($"[{current.ToString("D6")}/{total.ToString("D6")}] [UriFormatException] [R {entry.rID.ToString("D6")}] [C {entry.CourseID.ToString("D7")}] {entry.Name}");
                uri = new Uri("https://this-serves-no-purpose-but-to-get-visual-studio-to-SHUT-UP.com/");
                invalidUrl = true;
            }

            if(!invalidUrl)
            {
                HttpResponseMessage crawledPage;

                bool stopExecution = false;

                try
                {
                    crawledPage = client.Send(new HttpRequestMessage(HttpMethod.Head, uri));
                }
                catch (Exception)
                {
                    crawledPage = new HttpResponseMessage();
                    stopExecution = true;
                }

                if(!stopExecution)
                {
                    HttpStatusCode resourceStatusCode = crawledPage.StatusCode;

                    if (resourceStatusCode.ToString() == "OK")
                    {
                        Log.Logger.Information($"[{current.ToString("D6")}/{total.ToString("D6")}] [{resourceStatusCode.ToString()}] [R {entry.rID.ToString("D6")}] [C {entry.CourseID.ToString("D6")}] {entry.Name}");
                    }
                    else
                    {
                        Log.Logger.Error($"[{current.ToString("D6")}/{total.ToString("D6")}] [{resourceStatusCode.ToString()}] [R {entry.rID.ToString("D6")}] [C {entry.CourseID.ToString("D6")}] {entry.Name}");
                        Log.Logger.Warning($"https://moodle.homeschoolconnectionsonline.com/course/view.php?id={entry.CourseID.ToString()}");
                    }
                }
            }
        }
    }

    public class MoodleURLEntry
    {
        public int rID { get; set; }
        public int CourseID { get; set; }
        public string Name { get; set; }
        public string URL { get; set; }

        public MoodleURLEntry(int rID, int courseID, string name, string url)
        {
            this.rID = rID;
            this.CourseID = courseID;
            this.Name = name;
            this.URL = url;
        }
    }

    public class DBConnection
    {
        public string? Server { get; set; }
        public string? DatabaseName { get; set; }
        public string? UserName { get; set; }
        public string? Password { get; set; }

        public MySqlConnection? Connection { get; set; }

        private static DBConnection? _instance = null;

        public static DBConnection Instance()
        {
            if (_instance == null)
                _instance = new DBConnection();
            return _instance;
        }

        public bool IsConnect()
        {
            if (Connection == null)
            {
                Log.Logger.Information("Connecting to MySQL Database...");
                Log.Logger.Information($"Database Address: {Server}");
                Log.Logger.Information($"Database Name: {DatabaseName}");
                Log.Logger.Information($"Database Username: {UserName}");
                Log.Logger.Information($"Database Password: {Password}");

                if (String.IsNullOrEmpty(DatabaseName))
                    return false;
                string connection = string.Format("Server={0}; database={1}; UID={2}; password={3}", Server, DatabaseName, UserName, Password);
                Connection = new MySqlConnection(connection);
                Connection.Open();
            }
            return true;
        }

        public void Close()
        {
            try
            {
#pragma warning disable CS8602 // Dereference of a possibly null reference.
                Connection.Close();
#pragma warning restore CS8602 // Dereference of a possibly null reference.
            }
            catch (NullReferenceException e)
            {
                Console.WriteLine(e.ToString());
            }
        }
    }
}