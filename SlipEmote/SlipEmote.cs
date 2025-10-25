using BepInEx;
using BepInEx.Configuration;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;
using System;
using System.Net;
using MoCore;
using Subpixel;
using Cysharp.Threading.Tasks;
using System.Linq;

namespace SlipEmote
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    [BepInDependency("com.mosadie.mocore", BepInDependency.DependencyFlags.HardDependency)]
    [BepInProcess("Slipstream_Win.exe")]
    public class SlipEmote : BaseUnityPlugin, IMoPlugin, IMoHttpHandler
    {
        private static ConfigEntry<bool> debugLogs;
        internal static ManualLogSource Log;

        private static ConfigEntry<bool> EnableHTTPEmotes;

        private static ConfigEntry<KeyboardShortcut> AssignEmoteKeybind;

        private static List<ConfigEntry<KeyboardShortcut>> EmoteKeybindList = new List<ConfigEntry<KeyboardShortcut>>();
        private static List<ConfigEntry<string>> EmoteKeybindAssignedEmoteList = new List<ConfigEntry<string>>();

        private static List<KeyCode> defaultKeys = [
            KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4, KeyCode.Alpha5, KeyCode.Alpha6, KeyCode.Alpha7, KeyCode.Alpha8, KeyCode.Alpha9, KeyCode.Alpha0
            ];

        private bool isSetup = false;
        private EmoteCatalogEntry emoteToSave = null;

        public static readonly string HTTP_PREFIX = "slipemote";

        public static readonly string COMPATIBLE_GAME_VERSION = "4.1595";
        public static readonly string GAME_VERSION_URL = "https://raw.githubusercontent.com/MoSadie/SlipEmote/refs/heads/main/versions.json";

        private void Awake()
        {
            try
            {
                SlipEmote.Log = base.Logger;

                if (!MoCore.MoCore.RegisterPlugin(this))
                {
                    Log.LogError("Failed to register plugin with MoCore. Please check the logs for more information.");
                    return;
                }

                debugLogs = Config.Bind("Debug", "DebugLogs", false, "Enable additional logging for debugging");

                EnableHTTPEmotes = Config.Bind("HTTP", "EnableHTTPEmotes", false, "Enable the ability to trigger emotes via HTTP requests. This will allow external programs to attempt to use emotes.");

                // Configure the emote keybinds

                AssignEmoteKeybind = Config.Bind("Keybinds", "AssignEmoteKeybind", new KeyboardShortcut(KeyCode.F1), "Keybind to start/cancel the process of setting an emote to an emote key.");

                for (int i = 0; i < defaultKeys.Count; i++)
                {
                    var emoteKeybind = Config.Bind("Keybinds", $"EmoteKeybind{i + 1}", new KeyboardShortcut(defaultKeys[i]), $"Keybind to trigger emote key {i + 1}");
                    EmoteKeybindList.Add(emoteKeybind);
                    var emoteAssigned = Config.Bind("Emotes", $"Emote{i + 1}", "", $"The emote assigned to emote key {i + 1}. Leave empty for no emote.");
                    EmoteKeybindAssignedEmoteList.Add(emoteAssigned);
                }

                Logger.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} is loaded!");
                isSetup = true;
            } catch (Exception e)
            {
                Log.LogError("An error occurred while starting the plugin.");
                Log.LogError(e.Message);
            }

        }

        private void Update()
        {
            if (!isSetup) return;

            try
            {
                // Check if the assign emote keybind is pressed
                if (AssignEmoteKeybind.Value.IsDown())
                {
                    if (emoteToSave == null)
                    {
                        // Check if the last emote used exists, if so set it to emoteToSave
                        var localCrewmate = Mainstay<LocalCrewSelectionManager>.Main.GetSelectedLocalCrewmate();
                        if (localCrewmate == null)
                        {
                            DebugLogWarn("Attempted to start emote assignment, but no local crewmate is found!");
                            return;
                        }

                        if (localCrewmate.Crewmate == null || localCrewmate.Crewmate.EmoteController == null)
                        {
                            DebugLogWarn("Attempted to start emote assignment, but no crewmate or emote controller is found!");
                            return;
                        }

                        var emoteController = localCrewmate.Crewmate.EmoteController;

                        if (emoteController.LastEmoteUsed == null)
                        {
                            DebugLogWarn("Attempted to start emote assignment, but no last emote used is found! Please use an emote first, then press this key again!");
                            RecieveOrder("Please use an emote first, then press this key again to start assigning it to an emote key!");
                            return;
                        }

                        emoteToSave = emoteController.LastEmoteUsed;

                        DebugLogInfo($"Starting emote assignment process for emote: {emoteToSave.Doc.Id}");
                        RecieveOrder($"Ready to assign emote: {emoteToSave.Doc.Data.Name}. Press the desired emote key to assign it to, or press this key again to cancel.");
                    } else
                    {
                        // Cancel the emote assignment process
                        DebugLogInfo("Emote key assignment cancelled.");
                        RecieveOrder("Emote key assignment cancelled.");
                        emoteToSave = null;
                    }
                }

                // Check if any of the emote keybinds are pressed
                for (int i = 0; i < EmoteKeybindList.Count; i++)
                {
                    if (EmoteKeybindList[i].Value.IsDown())
                    {
                        if (emoteToSave != null)
                        {
                            // Assign the emote to this keybind
                            EmoteKeybindAssignedEmoteList[i].Value = emoteToSave.Doc.Id;
                            Config.Save();
                            DebugLogInfo($"Assigned emote {emoteToSave.Doc.Id} to emote key {i + 1}");
                            RecieveOrder($"Assigned emote {emoteToSave.Doc.Data.Name} to emote key {i + 1}");
                            emoteToSave = null;
                        }

                        var assignedEmote = EmoteKeybindAssignedEmoteList[i].Value;
                        if (!string.IsNullOrEmpty(assignedEmote))
                        {
                           
                            DebugLogInfo($"Emote Keybind {i + 1} Pressed, triggering emote: {assignedEmote}");

                            TryEmote(assignedEmote);
                        }
                        else
                        {
                            DebugLogInfo($"Emote Keybind {i + 1} Pressed, but no emote is assigned.");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.LogError("An error occurred during the Update process. " + e.Message);
                Log.LogError(e.StackTrace);
            }
        }

        internal static bool TryEmote(string emoteId)
        {
            // Check if the assigned emote exists and the user owns the emote
            var emoteInventory = Svc.Get<UserEmotesInventory>();

            if (emoteInventory == null)
            {
                Log.LogError("UserEmotesInventory service is null, cannot check owned emotes.");
                return false;
            }

            var emoteOwned = emoteInventory.IsEmoteUnlocked(emoteId);

            if (!emoteOwned)
            {
                Log.LogError($"Attempted to trigger emote {emoteId}, but the user does not own this emote.");
                RecieveOrder($"Something went wrong triggering that emote. Please try assigning it again.");
                return false;
            }

            // Trigger the assigned emote

            var localCrewmate = Mainstay<LocalCrewSelectionManager>.Main.GetSelectedLocalCrewmate();
            if (localCrewmate == null)
            {
                Log.LogWarning("Attempted to trigger emote, but no local crewmate is found!");
                return false;
            }

            if (localCrewmate.Crewmate == null || localCrewmate.Crewmate.EmoteController == null)
            {
                Log.LogWarning("Attempted to trigger emote, but no crewmate or emote controller is found!");
                return false;
            }

            var emoteController = localCrewmate.Crewmate.EmoteController;

            var staticData = Svc.Get<StaticData>();

            if (staticData == null)
            {
                Log.LogError("StaticData service is null, cannot retrieve emote data.");
                return false;
            }

            var emoteCatalog = staticData.Emotes;

            if (emoteCatalog == null)
            {
                Log.LogError("Emote catalog is null, cannot retrieve emote data.");
                return false;
            }

            var emoteEntry = emoteCatalog.GetEntryByEmoteId(emoteId);

            UniTaskExtensions.Forget<bool>(emoteController.Local_Emote(emoteEntry));
            return true;
        }

        internal static void RecieveOrder(string msg)
        {
            try
            {
                var localCrewmate = Mainstay<LocalCrewSelectionManager>.Main.GetSelectedLocalCrewmate();
                if (localCrewmate == null)
                {
                    DebugLogWarn("Attempted to send local order, but no local crewmate is found!");
                    return;
                }
                
                OrderVo local = OrderHelpers.CreateLocal(OrderIssuer.Nobody, OrderType.General, msg);
                Svc.Get<Subpixel.Events.Events>().Dispatch<OrderGivenEvent>(new OrderGivenEvent(local));
            }
            catch (Exception e)
            {
                Log.LogError("An error occurred while trying to receive a local order.");
                Log.LogError(e);
            }
        }

        internal static void DebugLogInfo(string message)
        {
            if (debugLogs.Value)
            {
                Log.LogInfo(message);
            }
        }

        internal static void DebugLogWarn(string message)
        {
            if (debugLogs.Value)
            {
                Log.LogWarning(message);
            }
        }

        internal static void DebugLogError(string message)
        {
            if (debugLogs.Value)
            {
                Log.LogError(message);
            }
        }

        internal static void DebugLogDebug(string message)
        {
            if (debugLogs.Value)
            {
                Log.LogDebug(message);
            }
        }

        public string GetCompatibleGameVersion()
        {
            return COMPATIBLE_GAME_VERSION;
        }

        public string GetVersionCheckUrl()
        {
            return GAME_VERSION_URL;
        }

        public BaseUnityPlugin GetPluginObject()
        {
            return this;
        }

        public IMoHttpHandler GetHttpHandler()
        {
            return this;
        }

        public string GetPrefix()
        {
            return HTTP_PREFIX;
        }

        public HttpListenerResponse HandleRequest(HttpListenerRequest request, HttpListenerResponse response)
        {
            DebugLogInfo("Handling request.");
            try
            {
                HttpStatusCode status;
                string responseString;

                string pathUrl = request.RawUrl.Split('?', 2)[0];

                if (pathUrl.ToLower().Equals($"/{HTTP_PREFIX}/emotes"))
                {
                    DebugLogInfo("Attempting to list available emotes");

                    // Get owned emotes
                    var emoteInventory = Svc.Get<UserEmotesInventory>();

                    if (emoteInventory == null)
                    {
                        Log.LogError("UserEmotesInventory service is null, cannot check owned emotes.");
                        status = HttpStatusCode.InternalServerError;
                        responseString = "{\"error\": \"Unable to get user emote inventory\"}";
                    }
                    else
                    {
                        EmotesCatalog emoteCatalog = Svc.Get<StaticData>().Emotes;

                        if (emoteCatalog == null)
                        {
                            Log.LogError("Failed to get emote catalog.");
                            status = HttpStatusCode.InternalServerError;
                            responseString = "{\"error\": \"Unable to get emote catalog\"}";
                        }
                        else
                        {
                            var emotes = new Dictionary<string, EmoteModel>();

                            foreach (string emoteId in emoteInventory.UnlockedEmoteIds)
                            {
                                var emoteCatalogEntry = emoteCatalog.GetEntryByEmoteId(emoteId);
                                if (emoteCatalogEntry == null)
                                {
                                    Log.LogWarning($"Emote with ID {emoteId} is unlocked but not found in the emote catalog.");
                                    continue;
                                }

                                emotes.Add(emoteId, emoteCatalogEntry.Doc.Data);
                            }

                            status = HttpStatusCode.OK;
                            responseString = Newtonsoft.Json.JsonConvert.SerializeObject(new { emotes });
                        }
                    }
                }
                else if (pathUrl.ToLower().Equals($"/{HTTP_PREFIX}/allemotes"))
                {
                    EmotesCatalog emoteCatalog = Svc.Get<StaticData>().Emotes;

                    if (emoteCatalog == null)
                    {
                        Log.LogError("Failed to get emote catalog.");
                        status = HttpStatusCode.InternalServerError;
                        responseString = "{\"error\": \"Unable to get emote catalog\"}";
                    }
                    else
                    {
                        var emoteInventory = Svc.Get<UserEmotesInventory>();

                        if (emoteInventory == null)
                        {
                            Log.LogError("UserEmotesInventory service is null, cannot check owned emotes.");
                            status = HttpStatusCode.InternalServerError;
                            responseString = "{\"error\": \"Unable to get user emote inventory\"}";
                        }
                        else
                        {
                            var emotes = new Dictionary<string, EmoteModel>();
                            foreach (var emoteEntry in emoteCatalog.GetAllEntriesBySortOrder())
                            {
                                if (EmotesHelpers.ShouldShowInShopForUser(emoteCatalog, emoteInventory, emoteEntry))
                                    emotes.Add(emoteEntry.Doc.Id, emoteEntry.Doc.Data);
                            }
                            status = HttpStatusCode.OK;
                            responseString = Newtonsoft.Json.JsonConvert.SerializeObject(new { emotes });
                        }
                    }
                }
                else if (pathUrl.ToLower().StartsWith($"/{HTTP_PREFIX}/tryemote"))
                {
                    if (!EnableHTTPEmotes.Value)
                    {
                        DebugLogWarn("tryemote endpoint called, but HTTP emotes are disabled in the config.");
                        status = HttpStatusCode.Forbidden;
                        responseString = "{\"error\": \"HTTP emotes are disabled in the config\"}";
                    }
                    else if (request.QueryString.AllKeys.Contains("emoteId"))
                    {
                        var emoteId = request.QueryString["emoteId"];

                        if (emoteId == null)
                        {
                            Log.LogWarning("tryemote endpoint called with null emoteId query parameter.");
                            status = HttpStatusCode.BadRequest;
                            responseString = "{\"error\": \"Invalid emoteId query parameter\"}";
                        }
                        else
                        {
                            DebugLogInfo($"Attempting to trigger emote with ID: {emoteId}");
                            status = HttpStatusCode.OK;
                            responseString = "{\"message\": \"Attempted to trigger emote\"}";

                            ThreadingHelper.Instance.StartSyncInvoke(() =>
                            {
                                TryEmote(emoteId);
                            });
                        }
                    }
                    else
                    {
                        Log.LogWarning("tryemote endpoint called without emoteId query parameter.");
                        status = HttpStatusCode.BadRequest;
                        responseString = "{\"error\": \"Missing emoteId query parameter\"}";
                    }
                }
                else
                {
                    status = HttpStatusCode.NotFound;
                    responseString = "{\"error\": \"Endpoint not found\"}";
                }

                response.StatusCode = (int)status;
                response.Headers.Add("Content-Type", "application/json");
                byte[] buffer = System.Text.Encoding.UTF8.GetBytes(responseString);
                response.ContentLength64 = buffer.Length;
                response.OutputStream.Write(buffer, 0, buffer.Length);
                response.OutputStream.Close();
                DebugLogInfo($"Request handled with status code: {status}");
            }
            catch (Exception e)
            {
                Log.LogError("An error occurred while handling the HTTP request.");
                Log.LogError(e.Message);
                Log.LogError(e.StackTrace);
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
                response.Headers.Add("Content-Type", "application/json");
                byte[] buffer = System.Text.Encoding.UTF8.GetBytes("{\"error\": \"Internal server error\"}");
                response.ContentLength64 = buffer.Length;
                response.OutputStream.Write(buffer, 0, buffer.Length);
                response.OutputStream.Close();
                return response;
            }

            return response;
        }
    }
}
