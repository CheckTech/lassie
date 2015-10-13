using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Lassie
{
    class MainClass
    {
        public static int Main(string[] args)
        {
            Console.WriteLine("Lassie 0.1-alpha\n");

            var platform = WhichPlatform();
            string configFile, configDir;

            switch (platform)
            {
                case Platform.Mac:
                    // do something
                    // .mcneel
                    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    configDir = Path.Combine(home, ".mcneel");
                    Directory.CreateDirectory(configDir);
                    configFile = Path.Combine(configDir, "lassie.json");
                    break;
                case Platform.Windows:
                    // do something else
                    // %appdata%
                    configDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "McNeel");
                    Directory.CreateDirectory(configDir);
                    configFile = Path.Combine(configDir, "lassie.json");
                    break;
                default:
                    // only supports windows and mac os x
                    Console.WriteLine("Lassie supports Windows and Mac OS X only. Sorry.");
                    return 0;
            }

            // check for stored credentials (and store credentials)

            string token;
            if (File.Exists(configFile))
            {
                var config = JsonConvert.DeserializeObject<AuthorizationsResponse>(File.ReadAllText(configFile));
                token = config.Token;
                // TODO: reset if >month since last updated
            }
            else
            {

                Console.WriteLine("No credentials found. Please enter your GitHub username and password.");
                Console.WriteLine("I promise not to do anything with these details except exchange them for a \"personal access token\" which will be used to authorization.\n");

                // get the username

                Console.Write("Username: ");
                string user = Console.ReadLine();

                // get the pasword
                // http://stackoverflow.com/questions/3404421/password-masking-console-application

                Console.Write("Password: ");
                //string pass = Console.ReadLine();
                string pass = "";
                ConsoleKeyInfo key;

                do
                {
                    key = Console.ReadKey(true);

                    // Backspace Should Not Work
                    if (key.Key != ConsoleKey.Backspace && key.Key != ConsoleKey.Enter)
                    {
                        pass += key.KeyChar;
                        Console.Write("*");
                    }
                    else
                    {
                        if (key.Key == ConsoleKey.Backspace && pass.Length > 0)
                        {
                            pass = pass.Substring(0, (pass.Length - 1));
                            Console.Write("\b \b");
                        }
                    }
                }
                // Stops Receving Keys Once Enter is Pressed
                while (key.Key != ConsoleKey.Enter);

                Console.WriteLine();
                //Console.WriteLine("The Password You entered is : " + pass);

                // https://developer.github.com/v3/oauth_authorizations/#create-a-new-authorization
                AuthorizationsResponse response;
                try
                {
                    response = Foo(user, pass).Result; // I know I'm not using async properly...
                    token = response.Token;
                }
                catch (Exception e)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Error.WriteLine("Something went wrong during authentication ({0}).", e.InnerException.Message);
                    Console.ResetColor();
                    return 1;
                }

                File.WriteAllText(configFile, JsonConvert.SerializeObject(response, Formatting.Indented));
            }

            // now it begins

            string repo = args[0];
            string path = args[1];
            string commitish = args[2];
            string outFilename = args[3];

            try
            {
                string fileContents = Bar(repo, path, commitish, token).Result;
                if (platform == Platform.Windows)
                {
                    string normalised = Regex.Replace(fileContents, @"\r\n|\n\r|\n|\r", "\r\n");
                    fileContents = normalised;
                }
                File.WriteAllText(outFilename, fileContents);
            }
            catch (Exception e)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine("Something went wrong fetching the file ({0}).", e.InnerException.Message);
                Console.ResetColor();
                return 1;
            }

            Console.WriteLine("Contents of {0}/{1}@{2} written to {3}", args);

            return 0;
        }

        public static async Task<AuthorizationsResponse> Foo(string user, string pass)
        {
            using (var client = new HttpClient())
            {
                client.BaseAddress = new Uri("https://api.github.com");
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));
                client.DefaultRequestHeaders.Add("User-Agent", "Lassie");
                var byteArray = Encoding.ASCII.GetBytes(user + ":" + pass);
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));

                var content = new AuthorizationsContent();
                content.
                Scopes = new string[] { "repo" };
                string profile = System.Environment.UserName;
                string hostname = System.Environment.MachineName;
                content.Note = string.Format("lassie for {0}@{1}", profile, hostname);

                var contentString = JsonConvert.SerializeObject(content);
                var stringContent = new StringContent(contentString);

                var response = await client.PostAsync("authorizations", stringContent);
                string json;

                if (!response.IsSuccessStatusCode)
                {
                    try
                    {
                        // TODO: 2FA
                        json = await response.Content.ReadAsStringAsync();
                        var obj = JsonConvert.DeserializeObject(json);
                        string formatted = JsonConvert.SerializeObject(obj, Formatting.Indented);
                        Console.WriteLine(formatted);
                        var data = JObject.Parse(json);
                        //Console.ForegroundColor = ConsoleColor.Red;
                        //Console.Error.WriteLine(data["message"]);
                        //Console.ResetColor();
                        throw new InvalidOperationException(data["message"].ToString());
                    }
                    catch (JsonException e)
                    {
                        throw new InvalidOperationException("Couldn't parse JSON error message.", e);
                    }
                }

                json = response.Content.ReadAsStringAsync().Result;
                return JsonConvert.DeserializeObject<AuthorizationsResponse>(json);
            }
        }

        public class AuthorizationsContent
        {
            [JsonProperty("scopes")]
            public string[] Scopes;
            [JsonProperty("note")]
            public string Note;
        }

        public class AuthorizationsResponse
        {
            [JsonProperty("token")]
            public string Token;
            [JsonProperty("created_at")]
            public string CreatedAt;
            [JsonProperty("updated_at")]
            public string UpdatedAt;
        }

        public static async Task<string> Bar(string repo, string path, string commitish, string token)
        {
            using (var client = new HttpClient())
            {
                client.BaseAddress = new Uri("https://api.github.com");
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));
                client.DefaultRequestHeaders.Add("User-Agent", "Lassie");
                //var byteArray = Encoding.ASCII.GetBytes(token);
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("token", token);

                var endpoint = string.Format("repos/{0}/contents/{1}?ref={2}", repo, path, commitish);

                var response = await client.GetAsync(endpoint);
                string json;

                if (!response.IsSuccessStatusCode)
                {
                    try
                    {

                        json = await response.Content.ReadAsStringAsync();
                        var obj = JsonConvert.DeserializeObject(json);
                        string formatted = JsonConvert.SerializeObject(obj, Formatting.Indented);
                        Console.WriteLine(formatted);
                        var data = JObject.Parse(json);
                        //Console.ForegroundColor = ConsoleColor.Red;
                        //Console.Error.WriteLine(data["message"]);
                        //Console.ResetColor();
                        throw new InvalidOperationException(data["message"].ToString());
                    }
                    catch (JsonException e)
                    {
                        throw new InvalidOperationException("Couldn't parse JSON error message.", e);
                    }
                }

                json = response.Content.ReadAsStringAsync().Result;
                //var obj2 = JsonConvert.DeserializeObject<Object>(json);
                var r = JObject.Parse(json);
                string content = r["content"].ToString();
                // TODO: check if "type" is "file"
                return Encoding.UTF8.GetString(Convert.FromBase64String(content));
            }

            //Console.WriteLine ("Hello World!");
        }

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
    }
}
