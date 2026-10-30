using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using Photon.Pun;
using Photon.Realtime;
using Steamworks;
using Steamworks.Data;

namespace NekogiriMod
{
    [BepInPlugin("kirigiri.repo.nekogiri", "Nekogiri", "1.1.0.3")]
    public class NekogiriMod : BaseUnityPlugin
    {
        private void Awake()
        {
            // Set up plugin logging
            Logger.LogInfo(@"
 ██ ▄█▀ ██▓ ██▀███   ██▓  ▄████  ██▓ ██▀███   ██▓
 ██▄█▒ ▓██▒▓██ ▒ ██▒▓██▒ ██▒ ▀█▒▓██▒▓██ ▒ ██▒▓██▒
▓███▄░ ▒██▒▓██ ░▄█ ▒▒██▒▒██░▄▄▄░▒██▒▓██ ░▄█ ▒▒██▒
▓██ █▄ ░██░▒██▀▀█▄  ░██░░▓█  ██▓░██░▒██▀▀█▄  ░██░
▒██▒ █▄░██░░██▓ ▒██▒░██░░▒▓███▀▒░██░░██▓ ▒██▒░██░
▒ ▒▒ ▓▒░▓  ░ ▒▓ ░▒▓░░▓   ░▒   ▒ ░▓  ░ ▒▓ ░▒▓░░▓  
░ ░▒ ▒░ ▒ ░  ░▒ ░ ▒░ ▒ ░  ░   ░  ▒ ░  ░▒ ░ ▒░ ▒ ░
░ ░░ ░  ▒ ░  ░░   ░  ▒ ░░ ░   ░  ▒ ░  ░░   ░  ▒ ░
░  ░    ░     ░      ░        ░  ░     ░      ░  
                                                 
");
            Logger.LogInfo("Nekogiri has loaded!");

            Instance = this;
            var harmony = new Harmony("kirigiri.repo.nekogiri");

            // v0.5.0 moved Steam initialization into Platforms.Steam.SteamPlatform.
            // Patch Facepunch SteamClient.Init's appId argument instead of relying on
            // the removed SteamManager.Awake hook. This preserves the game's own
            // initialization flow and avoids initializing Steam twice.
            PatchIfPresent(harmony,
                AccessTools.Method(typeof(SteamClient), "Init", new Type[] { typeof(uint), typeof(bool) }),
                nameof(SteamClientInitPrefix), true);

            PatchIfPresent(harmony,
                AccessTools.Method(typeof(DataDirector), "PhotonSetAppId"),
                nameof(PhotonSetAppIdPrefix), true);

            PatchIfPresent(harmony,
                AccessTools.Method(typeof(MenuPageMain), "Start"),
                nameof(MenuPageMainStartPrefix), false);

            // The auth-ticket entry point differs between game versions.
            Type steamPlatformType = AccessTools.TypeByName("Platforms.Steam.SteamPlatform");
            MethodBase authMethod = steamPlatformType == null ? null : AccessTools.Method(steamPlatformType, "SetPhotonAuthTicket");
            if (authMethod == null)
            {
                Type steamManagerType = AccessTools.TypeByName("SteamManager");
                authMethod = steamManagerType == null ? null : AccessTools.Method(steamManagerType, "SendSteamAuthTicket");
            }
            PatchIfPresent(harmony, authMethod, nameof(AuthTicketPrefix), false);

            Logger.LogInfo("Nekogiri v1.1.0.2 patches installed for the current game version.");
            Logger.LogInfo("Made with <3 By Kirigiri \nhttps://discord.gg/zn2a2A65My");
        }

        // The custom method to replace the original Start method
        private void CustomStart()
        {
            Logger.LogInfo("Custom Start method executed!");

            string configFilePath = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Kirigiri.ini");

            // Default App IDs
            string appIdRealtime = "99515094-45b7-4c98-ab70-a448c548c83d";
            string appIdVoice = "dfc40a01-c3a4-4395-9707-b71118816d2b";
            bool useDefaultRepoServers = false;

            if (File.Exists(configFilePath))
            {
                try
                {
                    var settings = File.ReadAllLines(configFilePath)
                                       .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith(";"))
                                       .Select(line => line.Split('='))
                                       .Where(parts => parts.Length == 2)
                                       .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim());

                    // Check for UseDefaultRepoServers flag
                    if (settings.TryGetValue("UseDefaultRepoServers", out string useDefaultValue) && useDefaultValue == "1")
                    {
                        useDefaultRepoServers = true;
                    }

                    // If not using defaults, try to get AppIdRealtime and AppIdVoice from INI file
                    if (!useDefaultRepoServers)
                    {
                        if (settings.TryGetValue("AppIdRealtime", out string iniAppIdRealtime) && !string.IsNullOrWhiteSpace(iniAppIdRealtime))
                        {
                            appIdRealtime = iniAppIdRealtime;
                            Debug.Log($"[Photon] AppIdRealtime from INI: {appIdRealtime}");
                        }

                        if (settings.TryGetValue("AppIdVoice", out string iniAppIdVoice) && !string.IsNullOrWhiteSpace(iniAppIdVoice))
                        {
                            appIdVoice = iniAppIdVoice;
                            Debug.Log($"[Photon] AppIdVoice from INI: {appIdVoice}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Error reading INI file, using default App IDs. Details: {ex.Message}");
                }
            }
            else
            {
                Logger.LogWarning($"INI file not found at {configFilePath}, using default App IDs.");
                useDefaultRepoServers = true; // Use default if the INI file is missing
            }

            // Apply the final AppId settings based on the condition
            PhotonNetwork.PhotonServerSettings.AppSettings.AppIdRealtime = appIdRealtime;
            PhotonNetwork.PhotonServerSettings.AppSettings.AppIdVoice = appIdVoice;
        }





        // Read the configured Steam App ID. The Harmony prefix below changes the
        // argument passed by the game's own SteamPlatform.Initialise() method.
        private uint ReadSteamAppId()
        {
            string configFilePath = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Kirigiri.ini");
            const uint defaultAppId = 3241660U;
            uint appId = defaultAppId;

            try
            {
                if (!File.Exists(configFilePath))
                {
                    Logger.LogWarning("Kirigiri.ini not found; using the game's default Steam App ID " + defaultAppId + ".");
                    return appId;
                }

                var settings = File.ReadAllLines(configFilePath)
                    .Where(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith(";") && !line.TrimStart().StartsWith("//"))
                    .Select(line => line.Split(new char[] { '=' }, 2))
                    .Where(parts => parts.Length == 2)
                    .GroupBy(parts => parts[0].Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Last()[1].Trim(), StringComparer.OrdinalIgnoreCase);

                if (settings.TryGetValue("UseDefaultRepoServers", out string useDefault) && useDefault == "1")
                {
                    Logger.LogInfo("Using default R.E.P.O. Steam App ID " + defaultAppId + ".");
                    return defaultAppId;
                }

                if (settings.TryGetValue("SteamAppId", out string configured) && uint.TryParse(configured, out uint parsed) && parsed > 0)
                    appId = parsed;
                else
                    Logger.LogWarning("SteamAppId missing or invalid in Kirigiri.ini; using " + defaultAppId + ".");
            }
            catch (Exception ex)
            {
                Logger.LogError("Could not read SteamAppId: " + ex.Message + ". Using " + defaultAppId + ".");
            }

            Logger.LogInfo("Steam App ID selected by Nekogiri: " + appId);
            return appId;
        }

        private static NekogiriMod Instance;

        private static void SteamClientInitPrefix(ref uint __0)
        {
            if (Instance == null) return;
            __0 = Instance.ReadSteamAppId();
        }

        private static bool PhotonSetAppIdPrefix()
        {
            if (Instance != null) Instance.CustomStart();
            return false; // The custom Photon App IDs replace the game's defaults.
        }

        private static bool MenuPageMainStartPrefix()
        {
            if (Instance != null) Instance.WelcomeMessage();
            return true; // Keep the game's original menu startup.
        }

        private static bool AuthTicketPrefix()
        {
            if (Instance != null) Instance.CustomAuth();
            return false; // Nekogiri supplies the configured Photon auth values.
        }

        private void PatchIfPresent(Harmony harmony, MethodBase original, string prefixName, bool required)
        {
            if (original == null)
            {
                string message = "Could not find patch target for " + prefixName + ".";
                if (required) Logger.LogError(message); else Logger.LogWarning(message);
                return;
            }

            MethodInfo prefix = AccessTools.Method(typeof(NekogiriMod), prefixName);
            if (prefix == null)
            {
                Logger.LogError("Could not find Nekogiri prefix " + prefixName + ".");
                return;
            }

            harmony.Patch(original, prefix: new HarmonyMethod(prefix));
            Logger.LogInfo("Patched " + original.DeclaringType.FullName + "." + original.Name);
        }


        public void CustomAuth()
        {
            Logger.LogInfo("Custom Auth method executed!");
            string configFilePath = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Kirigiri.ini");
            string authSetting = "Steam"; // Default value for Auth setting if not found or invalid
            bool useDefaultRepoServers = false;

            try
            {
                if (File.Exists(configFilePath))
                {
                    // Read all lines from the INI file
                    var settings = File.ReadAllLines(configFilePath)
                                       .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith(";"))
                                       .Select(line => line.Split('='))
                                       .Where(parts => parts.Length == 2)
                                       .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim());

                    // Check for UseDefaultRepoServers flag
                    if (settings.TryGetValue("UseDefaultRepoServers", out string useDefaultValue) && useDefaultValue == "1")
                    {
                        useDefaultRepoServers = true;
                    }

                    // If not using defaults, try to get Auth setting from the INI file
                    if (!useDefaultRepoServers)
                    {
                        if (settings.ContainsKey("Auth"))
                        {
                            authSetting = settings["Auth"];
                        }
                        else
                        {
                            Logger.LogWarning("Auth setting not found in the INI file, defaulting to 'Steam'.");
                        }
                    }
                }
                else
                {
                    Logger.LogWarning($"Settings file not found at {configFilePath}. Using default Auth value 'Steam'.");
                    useDefaultRepoServers = true; // Use default if the INI file is missing
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error reading Auth setting from INI: {ex.Message}. Defaulting to 'Steam'.");
            }

            // Map the string to the corresponding CustomAuthenticationType
            PhotonNetwork.AuthValues = new AuthenticationValues();
            PhotonNetwork.AuthValues.UserId = SteamClient.SteamId.ToString();

            CustomAuthenticationType authType = CustomAuthenticationType.Steam; // Default to Steam

            // Check the Auth setting and set the corresponding authentication type
            switch (authSetting.ToLower())
            {
                case "custom":
                    authType = CustomAuthenticationType.Custom;
                    break;
                case "steam":
                    authType = CustomAuthenticationType.Steam;
                    break;
                case "facebook":
                    authType = CustomAuthenticationType.Facebook;
                    break;
                case "oculus":
                    authType = CustomAuthenticationType.Oculus;
                    break;
                case "playstation4":
                    authType = CustomAuthenticationType.PlayStation4;
                    break;
                case "xbox":
                    authType = CustomAuthenticationType.Xbox;
                    break;
                case "viveport":
                    authType = CustomAuthenticationType.Viveport;
                    break;
                case "nintendoswitch":
                    authType = CustomAuthenticationType.NintendoSwitch;
                    break;
                case "playstation5":
                    authType = CustomAuthenticationType.PlayStation5;
                    break;
                case "epic":
                    authType = CustomAuthenticationType.Epic;
                    break;
                case "facebookgaming":
                    authType = CustomAuthenticationType.FacebookGaming;
                    break;
                case "none":
                default:
                    authType = CustomAuthenticationType.None;
                    break;
            }

            PhotonNetwork.AuthValues.AuthType = authType;

            // Add the Auth parameter (e.g., the Steam ticket)
            string value = this.GetSteamAuthTicket(out this.steamAuthTicket);
            PhotonNetwork.AuthValues.AddAuthParameter("ticket", value);

            Logger.LogInfo($"Patched Auth to {PhotonNetwork.AuthValues.AuthType}!");
        }


        private void WelcomeMessage()
        {
            string configFilePath = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Kirigiri.ini");

            try
            {
                if (File.Exists(configFilePath))
                {
                    // Read all lines from the INI file while preserving sections and comments
                    var lines = File.ReadAllLines(configFilePath).ToList();
                    bool welcomeReadUpdated = false;

                    for (int i = 0; i < lines.Count; i++)
                    {
                        // Look for the line containing "FirstLaunch"
                        if (lines[i].StartsWith("FirstLaunch"))
                        {
                            // If FirstLaunch is 1, show the message and update to 0
                            if (lines[i].Contains("FirstLaunch=1"))
                            {
                                // Show the welcome message with the correct PagePopUp signature
                                MenuManager.instance.PagePopUp(
                                    "Nekogiri",
                                    UnityEngine.Color.magenta,
                                    "<size=20>This mod has been made by Kirigiri.\nMake sure to create an account on <color=#808080>https://www.photonengine.com/</color> and to fill the values inside the <color=#cc00ff>Kirigiri.ini</color> file !\nThis message will appear only once, Have fun !",
                                    "OK",
                                    true // richText enabled
                                );

                                // Update FirstLaunch to 0
                                lines[i] = "FirstLaunch=0";
                                welcomeReadUpdated = true;
                            }
                            break;
                        }
                    }

                    // If the FirstLaunch value was updated, write back the modified lines
                    if (welcomeReadUpdated)
                    {
                        File.WriteAllLines(configFilePath, lines);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error reading or updating Kirigiri.ini: {ex.Message}");
            }
        }




        private string GetSteamAuthTicket(out AuthTicket ticket)
        {
            Debug.Log("Getting Steam Auth Ticket...");
            ticket = SteamUser.GetAuthSessionTicket(NetIdentity.LocalHost);
            System.Text.StringBuilder stringBuilder = new System.Text.StringBuilder();
            for (int i = 0; i < ticket.Data.Length; i++)
            {
                stringBuilder.AppendFormat("{0:x2}", ticket.Data[i]);
            }
            return stringBuilder.ToString();
        }

        internal AuthTicket steamAuthTicket;
    }
}
