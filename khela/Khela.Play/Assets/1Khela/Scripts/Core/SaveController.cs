using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace PlayCard.Core
{
    /// <summary>
    /// Lightweight local save manager. Most state comes from server JSON; this stores client flags (auth tokens, settings).
    /// Deterministic keys are required; do not use GetHashCode.
    /// </summary>
    [DefaultExecutionOrder(-500)]
    public static class SaveController
    {
        private const string SaveFileName = "client_save.json";
        private static readonly object Gate = new object();

        private static readonly Dictionary<string, ISaveObject> Registered = new Dictionary<string, ISaveObject>();
        private static SaveState _state = new SaveState();
        private static bool _loaded;
        private static bool _dirty;
        private static MonoBehaviour _runner;

        public static bool IsLoaded => _loaded;

        public static event Action OnLoaded;

        /// <summary>
        /// Initialize save system. A runner is required for coroutines (autosave). Creates a hidden runner if null.
        /// </summary>
        public static void Init(MonoBehaviour runner = null, float autoSaveIntervalSeconds = 60f)
        {
            if (_loaded) return;

            _runner = runner ?? CreateRunner();
            LoadFromDisk();

            if (autoSaveIntervalSeconds > 0f)
            {
                _runner.StartCoroutine(AutoSave(autoSaveIntervalSeconds));
            }

            Application.quitting += FlushOnQuit;
            OnLoaded?.Invoke();
        }

        public static void Register(ISaveObject saveObject)
        {
            if (saveObject == null || string.IsNullOrWhiteSpace(saveObject.Key))
                throw new ArgumentException("SaveObject must have a non-empty key.");

            lock (Gate)
            {
                Registered[saveObject.Key] = saveObject;
                var record = _state.GetRecord(saveObject.Key);
                if (record != null && !string.IsNullOrEmpty(record.Data))
                {
                    saveObject.LoadFromJson(record.Data);
                }
            }
        }

        public static void Unregister(ISaveObject saveObject)
        {
            if (saveObject == null) return;
            lock (Gate)
            {
                Registered.Remove(saveObject.Key);
            }
        }

        public static void MarkDirty()
        {
            _dirty = true;
        }

        public static void Save(bool force = false)
        {
            if (!_loaded) return;
            if (!force && !_dirty) return;

            lock (Gate)
            {
                var records = new List<Record>(Registered.Count);
                foreach (var kvp in Registered)
                {
                    var json = kvp.Value.SaveToJson();
                    records.Add(new Record { Key = kvp.Key, Data = json });
                }

                _state.Records = records;
                WriteStateToDisk(_state);
                _dirty = false;
            }
        }

        private static void LoadFromDisk()
        {
            try
            {
                var path = GetSavePath();
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    _state = JsonUtility.FromJson<SaveState>(json) ?? new SaveState();
                }
                else
                {
                    _state = new SaveState();
                }
                _loaded = true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SaveController] Failed to load save: {ex}");
                _state = new SaveState();
                _loaded = true;
            }
        }

        private static void WriteStateToDisk(SaveState state)
        {
            try
            {
                var path = GetSavePath();
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var json = JsonUtility.ToJson(state, false);
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SaveController] Failed to write save: {ex}");
            }
        }

        private static IEnumerator<YieldInstruction> AutoSave(float intervalSeconds)
        {
            var wait = new WaitForSeconds(intervalSeconds);
            while (true)
            {
                yield return wait;
                Save();
            }
        }

        private static void FlushOnQuit()
        {
            Save(true);
        }

        private static MonoBehaviour CreateRunner()
        {
            var go = new GameObject("[SaveController]");
            go.hideFlags = HideFlags.HideInHierarchy;
            UnityEngine.Object.DontDestroyOnLoad(go);
            return go.AddComponent<Runner>();
        }

        [Serializable]
        private class SaveState
        {
            public List<Record> Records = new List<Record>();

            public Record GetRecord(string key)
            {
                return Records.Find(r => r.Key == key);
            }
        }

        [Serializable]
        private class Record
        {
            public string Key;
            public string Data;
        }

        private class Runner : MonoBehaviour { }

        private static string GetSavePath()
        {
            return Path.Combine(Application.persistentDataPath, SaveFileName);
        }
    }
}
