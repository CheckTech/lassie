using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CredentialManagement;

namespace Lassie
{
    class MainClass
    {
        public static int Main(string[] args)
        {
            if (args.Length != 4)
            {
                Console.WriteLine("Usage: [mono] Lassie.exe <user>/<repo> <file> <branch|commit|tag> <out_file>");
                Console.Write("Lassie {0}", Properties.Resources.Version);
                Console.WriteLine(@"
                (\
               (\_\_^__o
        ___     `-'/ `_/
       '`--\______/  |
  '        /         |
`    .  ' `-`/.------'\^-' mic");

                return 3;
            }

            // get args

            string repo = args[0];
            string path = args[1];
            string commitish = args[2];
            string outFilename = args[3];

            // check platform
            Platform platform = WhichPlatform();

            // check for stored credentials

            string token = null, user = null, pass = null;

            // if windows, let's try to get the username/password from the credential manager
            // requires the user to have setup `git config --global credential.helper wincred`
            if (platform == Platform.Windows)
            {
                var creds = new CredentialSet("git:*"); // wild
                creds.Load();
                var credential = creds.FirstOrDefault(c => c.Target.EndsWith("github.com"));
                if (credential != null)
                {
                    //Credential credential = new Credential { Target = "git:https://<username>@github.com", Type = CredentialType.Generic };
                    credential.Load();
                    user = credential.Username;
                    pass = credential.Password;
                }
            }

            // if we didn't manage to get credentials from windows' credential manager, try legacy lassie.json config file
            if (user == null || pass == null)
            {
                string configFile = WhereConfig(platform);

                if (File.Exists(configFile))
                {
                    var config = JsonConvert.DeserializeObject<Config>(File.ReadAllText(configFile));
                    token = config.Token;
                    // TODO: reset if >month since last updated
                }
                else
                {
                    Console.Error.WriteLine("Couldn't find any github credentials on your system. Looks like Timmy's spending the night in the well.");
                    return 5;
                }
            }

            // now it begins

            
            string contents;
            try
            {
                // fetch the file contents
                if (token != null)
                {
                    contents = Fetch(repo, path, commitish, token);
                }
                else
                {
                    contents = Fetch(repo, path, commitish, user, pass);
                }

                // normalise line endings for windows
                if (platform == Platform.Windows)
                {
                    string normalised = Regex.Replace(contents, @"\r\n|\n\r|\n|\r", "\r\n");
                    contents = normalised;
                }

                // write
                File.WriteAllText(outFilename, contents);
            }
            catch (Exception e)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine("Something went wrong fetching the file ({0}).", e.InnerException.Message);
                Console.ResetColor();
                return 1;
            }

            Console.WriteLine("Contents of {0}/{1}@{2} successfully written to {3}", args);

            return 0;
        }

        
        /// <summary>
        /// Used for (de)serialisation of lassie.json
        /// </summary>
        public class Config
        {
            [JsonProperty("token")]
            public string Token;
            [JsonProperty("created_at")]
            public string CreatedAt;
            [JsonProperty("updated_at")]
            public string UpdatedAt;
        }

        public static string Fetch(string repo, string path, string commitish, string token)
        {
            var auth = new AuthenticationHeaderValue("token", token);
            return Fetch(repo, path, commitish, auth);
        }

        public static string Fetch(string repo, string path, string commitish, string user, string pass)
        {
            var byteArray = Encoding.ASCII.GetBytes(user + ":" + pass);
            var auth = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));
            return Fetch(repo, path, commitish, auth);
        }

        private static string Fetch(string repo, string path, string commitish, AuthenticationHeaderValue auth)
        {
            using (var client = new HttpClient())
            {
                client.BaseAddress = new Uri("https://api.github.com");
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));
                client.DefaultRequestHeaders.Add("User-Agent", "Lassie");
                client.DefaultRequestHeaders.Authorization = auth;

                var endpoint = string.Format("repos/{0}/contents/{1}?ref={2}", repo, path, commitish);

                var response = client.GetAsync(endpoint).Result;

                // get all the jsons
                string json = response.Content.ReadAsStringAsync().Result;
                JObject data;
                try
                {
                    data = JObject.Parse(json);
                }
                catch (JsonException e)
                {
                    throw new InvalidOperationException("Couldn't parse GitHub's error message.", e);
                }

                // bad response from github
                if (!response.IsSuccessStatusCode)
                {
                    // throw a tantrum and send github's error message back up the chain    
                    throw new InvalidOperationException(data["message"].ToString());
                }

                string content = data["content"].ToString();
                // TODO: check if "type" is "file"
                return Encoding.UTF8.GetString(Convert.FromBase64String(content));
            }
        }

        // helper methods to correctly determine platform
        // http://stackoverflow.com/questions/10138040/how-to-detect-properly-windows-linux-mac-operating-systems

        public enum Platform
        {
            Windows,
            Linux,
            Mac
        }

        public static Platform WhichPlatform()
        {
            switch (Environment.OSVersion.Platform)
            {
                case PlatformID.Unix:
                    // Well, there are chances MacOSX is reported as Unix instead of MacOSX.
                    // Instead of platform check, we'll do a feature checks (Mac specific root folders)
                    if (Directory.Exists("/Applications")
                        & Directory.Exists("/System")
                        & Directory.Exists("/Users")
                        & Directory.Exists("/Volumes"))
                        return Platform.Mac;
                    else
                        return Platform.Linux;

                case PlatformID.MacOSX:
                    return Platform.Mac;

                default:
                    return Platform.Windows;
            }
        }

        // where should we look for the config file?
        public static string WhereConfig(Platform platform)
        {
            string configDir;

            switch (platform)
            {
                case Platform.Mac:
                case Platform.Linux:
                    // do something
                    // .mcneel
                    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    configDir = Path.Combine(home, ".mcneel");
                    Directory.CreateDirectory(configDir);
                    return Path.Combine(configDir, "lassie.json");
                case Platform.Windows:
                default:
                    // do something else
                    // %appdata%
                    configDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "McNeel");
                    Directory.CreateDirectory(configDir);
                    return Path.Combine(configDir, "lassie.json");
            }
        }
    }
}
