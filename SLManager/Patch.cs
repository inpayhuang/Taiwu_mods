using Harmony12;
using Ionic.Zip;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Emit;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;


namespace Sth4nothing.SLManager
{
    [HarmonyPatch(typeof(Encoding), "GetEncoding", new Type[] { typeof(int) })]
    public static class Encoding_GetEncoding_Path
    {
        public static bool Prefix(int codepage, ref Encoding __result)
        {
            if (!Main.Enabled || codepage != 437)
                return true;

            __result = new I18N.West.CP437();
            return false;
        }
    }
    /// <summary>
    /// 替换Ionic.Zip.OffsetStream 以修正DotNetZip中<see cref="ZipFile.Read(Stream, TextWriter, Encoding, EventHandler{ReadProgressEventArgs})"/>的错误
    /// </summary>
    [HarmonyPatch(typeof(ZipFile), "Read", typeof(Stream), typeof(TextWriter), typeof(Encoding), typeof(EventHandler<ReadProgressEventArgs>))]
    public static class ZipFile_Read_Patch
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var insts = new List<CodeInstruction>(instructions);
            var idx = -1;
            for (int i = 0; i < insts.Count; i++)
            {
                if (i > 0 && i + 1 < insts.Count
                    && insts[i - 1].opcode == OpCodes.Ldarg_0
                    && insts[i].opcode == OpCodes.Newobj
                    && insts[i].operand is ConstructorInfo info
                    && info.DeclaringType != null
                    && info.DeclaringType.Name == "OffsetStream") // 定位修改点
                {
                    idx = i;
                    break;
                }
            }

            if (idx >= 0)
            {// 用自定义的 OffsetStream 替换
                insts[idx].operand = typeof(OffsetStream).GetConstructors().First();
                Main.Logger.Log("插入成功");
            }
            return insts;
        }
    }

    [HarmonyPatch(typeof(WorldMapSystem), "Start")]
    public static class WorldMapSystem_Start_Patch
    {
        private static readonly string[] btns =
        {
            "SystemButton,608",//系统设置
            "SaveActorsButton,723",//铭刻
            "EncyclopediaButton,609",//太吾百晓册
            "ScrollButton,607",//太吾传承
            "ReShowTrunEventButton,822",//时节回顾
            "HomeButton,612",//产业视图
            "ManageManPower,778",// 人力调度
            //"LegendBook,724",//解读奇书
        };

        private static readonly Tuple<string, string>[] extraBtns =
        {
            Tuple.Create("SaveButton", "Graphics/Buttons/StartGameButton"),//快速存档
            Tuple.Create("LoadButton", "Graphics/Buttons/StartGameButton_NoColor"),//快速读档
            Tuple.Create("LoadButtonList", "Graphics/Buttons/StartGameButton_NoColor"),//列表读档
        };

        private const float Size = 40f;
        private const float StartX = 1920f;

        public static void Postfix()
        {
            if (Main.Enabled)
            {
                UI.Load();

                float startX = StartX;
                Vector2 iconSize = new Vector2(Size, Size);
                Transform parent = GameObject.Find("ResourceBack").transform;

                // 调整其他按钮的位置、大小
                foreach (var btn in btns)
                {
                    var button = GameObject.Find(btn);
                    if (button == null)
                    {
                        Debug.LogWarning(btn + " not exist");
                        continue;
                    }
                    startX -= Size;
                    button.GetComponent<RectTransform>().sizeDelta = iconSize;
                    button.transform.localPosition = new Vector3(startX, -30f, 0f);
                }
                // 解读奇书
                startX -= Size;
                MissionSystem.instance.legendBookBtn.GetComponent<RectTransform>().sizeDelta = iconSize;
                MissionSystem.instance.legendBookBtn.transform.localPosition = new Vector3(startX, -30f, 0f);
                // 添加按钮
                foreach (var btn in extraBtns)
                {
                    startX -= Size;
                    var obj = UnityEngine.Object.Instantiate(
                        GameObject.Find("EncyclopediaButton,609"),
                        new Vector3(startX, -30f, 0),
                        Quaternion.identity);
                    obj.name = btn.Item1;
                    obj.tag = "SystemIcon";
                    obj.transform.SetParent(parent, false);
                    obj.transform.localPosition = new Vector3(startX, -30f, 0);
                    ((Image)obj.GetComponent<Selectable>().targetGraphic).sprite = Resources.Load<Sprite>(btn.Item2);
                    obj.GetComponent<RectTransform>().sizeDelta = iconSize;
                    obj.AddComponent<MyPointerClick>();
                    obj.GetComponent<Button>().onClick.RemoveAllListeners();
                    UnityEngine.Object.Destroy(obj.GetComponent<PointerClick>());
                }
            }
        }
    }

    /// <summary>
    /// 添加的按钮的点击事件
    /// </summary>
    public class MyPointerClick : MonoBehaviour, IPointerClickHandler
    {
        public void OnPointerClick(PointerEventData eventData)
        {
            if (!Main.Enabled) return;
            if (Main.isBackuping)
            {
                YesOrNoWindow.instance.SetYesOrNoWindow(-1,
                    "无法操作",
                    DateFile.instance.massageDate[701][2].
                        Replace("确定放弃所有未保存的游戏进度，返回主菜单吗？", "请稍待后台备份存档完成")
                        .Replace("返回到游戏的主菜单…\n", ""),
                    false,
                    true);
                return;
            }
            switch (gameObject.name)
            {
                case "LoadButtonList":
                    LoadFiles();
                    break;
                case "LoadButton":
                    YesOrNoWindow.instance.SetYesOrNoWindow(4646, "快速载入",
                        DateFile.instance.massageDate[701][2].Replace("返回主菜单", "载入旧存档")
                            .Replace("返回到游戏的主菜单…\n", ""), false, true);
                    break;
                case "SaveButton":
                    Main.ForceSave = true;
                    SaveDateFile.instance.SaveGameDate();
                    break;
            }
        }

        /// <summary>
        /// 加载所有存档
        /// </summary>
        public static void LoadFiles()
        {
            var files = Directory.GetFiles(SaveManager.BackPath,
                $"Date_{SaveManager.DateId}.save.???.zip",
                SearchOption.TopDirectoryOnly);
            Array.Sort(files, StringComparer.InvariantCulture);

            LoadFile.SavedFiles = new List<string>();

            if (File.Exists(Path.Combine(SaveManager.SavePath, "TW_Save_Date_0.twV0"))
                || File.Exists(Path.Combine(SaveManager.SavePath, "TW_Save_Date_0.tw")))
                LoadFile.SavedFiles.Add(SaveManager.SavePath);

            LoadFile.SavedFiles.AddRange(
                Main.settings.maxBackupToLoad > 0
                    ? files.Reverse().Take(Main.settings.maxBackupToLoad)
                    : files);

            LoadFile.ParseFiles();

            UI.Instance.ShowData();
        }
    }

    /// <summary>
    /// 读档
    /// </summary>
    [HarmonyPatch(typeof(OnClick), "Index")]
    public static class OnClick_Index_Patch
    {
        public static void Postfix()
        {
            if (!Main.Enabled) return;
            switch (OnClick.instance.ID)
            {
                case 4646:
                    LoadFile.DoLoad(SaveDateFile.instance.dateId);
                    break;
            }
        }
    }

    /// <summary>
    /// 读档
    /// </summary>
    [HarmonyPatch(typeof(DateFile), "LoadLegendBook")]
    public static class DateFile_LoadLegendBook_Patch
    {
        /// <summary>
        /// 需要清除的实例
        /// </summary>
        // 不同于从主菜单载入，在游戏内载入存档时很多游戏实例依然存在，它们会不停调用SingletonObject.getInstance<T>()。
        // 若T的实例未创建，则getInstance<T>方法在第一次被调用时会创建T的实例。这导致尽管在载入存档前执行
        // SingletonObject.ClearInstances()清除所有实例，载入存档时还会存在冲突的实例。所以在DateFile.LoadLegendBook()
        // 方法前将有可能还存在的冲突实例清除掉以便正常载入存档。
        private static readonly string[] instancesToRemove =
        {
            "JuniorXiangshuSystem",
            "LegendBookSystem",
            "TaichiDiscSystem",
            // "SpecialEffectSystem" //游戏V0.2.3.x：不再由Singleton载入，改由Subsystem载入
        };
        private static Dictionary<string, object> SingletonMap
            => singletonMap = singletonMap
                ?? ReflectionMethod.GetValue<SingletonObject, Dictionary<string, object>>(null, "m_SingletonMap");
        private static Dictionary<string, object> singletonMap;
        private static List<Type> DontClearList
            => dontClearList = dontClearList
                ?? ReflectionMethod.GetValue<SingletonObject, List<Type>>(null, "dontClearList");
        private static List<Type> dontClearList;
        /// <summary>
        /// 清除可能会冲突的实例(尽管禁用UIDate后再载入存档一般可以顺利进行，但游戏特殊情况仍会卡读档，在游戏开发稳定之前双保险)
        /// </summary>
        /// <returns></returns>
        private static bool Prefix(DateFile.LegendBook loadDate)
        {
            if (LoadFile.OnLoad)
            {
                LoadFile.OnLoad = false;
#if DEBUG
                var m_Container = ReflectionMethod.GetValue<SingletonObject, GameObject>(null, "m_Container");
                Main.Logger.Log($"DateFile_Loadloadlegend: Is m_Container null? {m_Container == null}");
                foreach(string key in m_SingletonMap.Keys)
                {
                    Main.Logger.Log($"DateFile_Loadloadlegend m_SingletonMap keys {key}");
                }
#endif
                // 清除需要清除的实例(仿自 SingletonObject.ClearInstances)
                foreach (var inst in instancesToRemove)
                {
                    if (SingletonMap.TryGetValue(inst, out object item) && !DontClearList.Contains(item))
                    {
                        (item as IDisposable)?.Dispose();
                        if (item is Component cmp)
                        {
                            UnityEngine.Object.Destroy(cmp);
                        }
                        SingletonMap.Remove(inst);
                    }
                }
                // 清除Subsystem中可能存在冲突的实例
                SubSystems.OnUnloadGameData();
            }
            return true;
        }

    }

    /// <summary>
    /// 存档的摘要信息
    /// </summary>
    public class SaveData
    {
        public string name;
        public int year;
        public int samsara;
        public int trun;
        public string position;
        public string playtime;

        [Obsolete]
        public SaveData()
        {
        }

        public SaveData(string name, int year, int samsara, int turn, string position, DateTime playtime) :
            this(name, year, samsara, turn, position, playtime.ToString("yyyy - MM - dd   [ HH : mm ]"))
        {
        }

        public SaveData(string name, int year, int samsara, int turn, string position,
            string playtime)
        {
            this.name = name;
            this.year = year;
            this.samsara = samsara;
            this.trun = turn;
            this.position = position;
            this.playtime = playtime;
        }

        public int GetTurn()
        {
            return Mathf.Max(DateFile.instance.GetDayTrun(trun), 0) / 2;
        }
    }

    public static class LoadFile
    {
        public static bool OnLoad { get; internal set; }
        /// <summary>
        /// 需要解析的压缩存档路径
        /// </summary>
        public static List<string> SavedFiles;

        /// <summary>
        /// 解析了的存档
        /// </summary>
        public static ConcurrentDictionary<string, SaveData> SavedInfos;

        public const string Format = "yyyy - MM - dd   [ HH : mm ]";

        /// <summary>
        /// 解析压缩存档列表
        /// </summary>
        public static void ParseFiles()
        {
            SavedInfos = new ConcurrentDictionary<string, SaveData>();

            Parallel.ForEach(SavedFiles, ParseThread);

            SavedFiles.Sort((f1, f2) =>
            {
                if (!SavedInfos.ContainsKey(f1))
                    return -1;
                if (!SavedInfos.ContainsKey(f2))
                    return 1;
                var t1 = DateTime.ParseExact(SavedInfos[f1].playtime, Format, null);
                var t2 = DateTime.ParseExact(SavedInfos[f2].playtime, Format, null);
                return -t1.CompareTo(t2);
            });
        }
        /// <summary>
        /// 解析存档
        /// </summary>
        /// <param name="file"></param>
        public static void ParseThread(string path)
        {
            try
            {
                Debug.Log("Parse: " + path);
                var data = Parse(path);
                SavedInfos[path] = data ?? throw new Exception("未能解析存档");
            }
            catch (Exception e)
            {
                Debug.Log("[ERROR]" + e);
            }
        }

        /// <summary>
        /// 解析指定压缩存档
        /// </summary>
        /// <param name="path">压缩存档路径</param>
        /// <returns></returns>
        public static SaveData Parse(string path)
        {
            return path.EndsWith(".zip") ? ParseZip(path) : ParseDirectory(path);
        }

        /// <summary>
        /// 存档为游戏存档
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public static SaveData ParseDirectory(string path)
        {
            SaveData data;
            if (File.Exists(Path.Combine(path, "date.json")))
            {
                var content = File.ReadAllText(Path.Combine(path, "date.json"));
                data = JsonConvert.DeserializeObject(content, typeof(SaveData)) as SaveData;
            }
            else if (!File.Exists(Path.Combine(path, "TW_Save_Date_0.twV0"))
                     && !File.Exists(Path.Combine(path, "TW_Save_Date_0.tw")))
            {
                throw new Exception(path);
            }
            else
            {
                var date = File.Exists(Path.Combine(path, "TW_Save_Date_0.twV0"))
                    ? ReadSaveDate(Path.Combine(path, "TW_Save_Date_0.twV0"), false)
                    : ReadSaveDate(Path.Combine(path, "TW_Save_Date_0.tw"), true);

                data = new SaveData(date._mainActorName, date._year, date._samsara,
                    date._dayTrun, date._playerSeatName, date._playTime);
                File.WriteAllText(Path.Combine(path, "date.json"),
                    JsonConvert.SerializeObject(data));
            }

            return data;
        }

        /// <summary>
        /// 存档为压缩包
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public static SaveData ParseZip(string path)
        {
            SaveData data;
            using (var zip = new ZipFile(path))
            {
                if (zip.ContainsEntry("date.json"))
                {
                    using (var stream = new MemoryStream())
                    {
                        zip.SelectEntries("date.json").First().Extract(stream);
                        stream.Seek(0, SeekOrigin.Begin);
                        using (var reader = new StreamReader(stream))
                        {
                            var serializer = JsonSerializer.Create();
                            data = serializer.Deserialize(reader,
                                typeof(SaveData)) as SaveData;
                        }
                    }
                }
                else if (!zip.ContainsEntry("TW_Save_Date_0.twV0")
                         && !zip.ContainsEntry("TW_Save_Date_0.tw"))
                {
                    throw new Exception(path); // 错误存档
                }
                else // 不含加速文件
                {
                    var tmp = Path.Combine(
                        Environment.GetEnvironmentVariable("TEMP"),
                        Guid.NewGuid() + ".tw");

                    bool rijndeal = true;
                    using (var stream = File.OpenWrite(tmp))
                    {
                        if (zip.ContainsEntry("TW_Save_Date_0.twV0"))
                        {
                            zip.SelectEntries("TW_Save_Date_0.twV0").First().Extract(stream);
                            rijndeal = false;
                        }
                        else if (zip.ContainsEntry("TW_Save_Date_0.tw"))
                        {
                            zip.SelectEntries("TW_Save_Date_0.tw").First().Extract(stream);
                            rijndeal = true;
                        }
                    }

                    var date = ReadSaveDate(tmp, rijndeal);

                    File.Delete(tmp);

                    data = new SaveData(date._mainActorName, date._year, date._samsara,
                        date._dayTrun, date._playerSeatName, date._playTime);
                    //  添加加速文件
                    zip.AddEntry("date.json", JsonConvert.SerializeObject(data));
                    zip.Save();
                }
            }

            return data;
        }

        /// <summary>
        /// 获取存档内容
        /// </summary>
        /// <param name="path"></param>
        /// <param name="rijndeal"></param>
        /// <returns></returns>
        private static DateFile.SaveDate ReadSaveDate(string path, bool rijndeal)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return ReflectionMethod.Invoke<SaveDateFile, DateFile.SaveDate>(
                SaveDateFile.instance,
                "GetData",
                path, typeof(DateFile.SaveDate), rijndeal // 参数
            );
        }

        /// <summary>
        /// 读取指定压缩存档
        /// </summary>
        /// <param name="file"></param>
        public static IEnumerator<object> Load(string file)
        {
            yield return new WaitForSeconds(0.01f);
            if (file.EndsWith(".zip"))
                Unzip(file);
            DoLoad(SaveManager.DateId);
        }

        /// <summary>
        /// 执行存档读取操作
        /// </summary>
        public static void DoLoad(int dataId)
        {
            OnLoad = true;
            // 防止UI活动生成新的SingletonObject实例
            UIDate.instance.gameObject.SetActive(false);
            // 来自DateFile.BackToStartMenu()方法，载入存档前清空，防止载入存档时载入奇书数据时卡档
            SingletonObject.ClearInstances();
            // 释放资源
            SubSystems.OnUnloadGameData();

            MainMenu.instance.SetLoadIndex(dataId);
        }



        /// <summary>
        /// 解压存档到游戏存档目录
        /// </summary>
        /// <param name="file"></param>
        public static void Unzip(string file)
        {
            using (var zip = new ZipFile(file))
            {
                Directory.GetFiles(SaveManager.SavePath, "*.tw*", SearchOption.TopDirectoryOnly)
                    .Do(File.Delete);

                zip.ExtractAll(SaveManager.SavePath, ExtractExistingFileAction.OverwriteSilently);
            }
        }

    }

    public static class SaveManager
    {
        public static SaveDateBackuper Backuper { get => SaveDateBackuper.GetInstance(); }
        public static int autoBackupRemain
        {
            get => ReflectionMethod.GetValue<SaveDateBackuper, int>(Backuper, "autoBackupRemain");
            set => ReflectionMethod.SetValue<SaveDateBackuper>(Backuper, "autoBackupRemain", value);
        }
        public const int AfterSaveBackup = 0,
            BeforeLoadingBackup = 1;

        /// <summary>
        /// 当前系统存档路径
        /// </summary>

        public static string SavePath
        {
            get => (ReflectionMethod.Invoke<SaveDateFile>(
                    SaveDateFile.instance,
                    "Dirpath",
                    new[] { typeof(int) },
                    -1) as string)
                .Replace('/', '\\');
        }

        /// <summary>
        /// SaveBackup的备份路径
        /// </summary>
        public static string BackPath
        {
            get => GetBackupStoragePath(SavePath);
        }

        /// <summary>
        /// 当前存档槽位置
        /// </summary>
        public static int DateId => SaveDateFile.instance.dateId;

        public static string Backup(int backupType)
        {
            if (!Backuper.CheckAllSaveFileExist(DateId))
            {
                Debug.Log("存档文件不全");
                return null;
            }
            switch (backupType)
            {
                case AfterSaveBackup:
                    return BackupAfterSave();

                case BeforeLoadingBackup:
                    return BackupBeforeLoad();

                default:
                    throw new ArgumentException("invalid backupType");
            }
        }

        private static readonly Regex reg = new Regex(@"Date_\d+\.save\.(.{3})\.zip");

        /// <summary>
        /// 执行存档后备份
        /// </summary>
        private static string BackupAfterSave()
        {
            if (Main.settings.maxBackupsToKeep == 0)
            {
                return null;
            }
            // 防止备份存档时进行其他操作会改变存档文件造成错误
            Main.isBackuping = true;

            Main.Logger.Log("开始备份存档");

            int backupIndex;

            // 获取所有当前存档的备份
            var backupFiles = Directory.GetFiles(
                BackPath,
                $"Date_{DateId}.save.???.zip",
                SearchOption.TopDirectoryOnly);
            Main.Logger.Log("当前存档数:" + backupFiles.Length);

            var maxIndex = backupFiles.Length == 0
                ? -1
                : backupFiles.Select(s => int.TryParse(reg.Match(s).Groups[1].Value, out var num) ? num : -1).Max();

            if (maxIndex < Main.settings.maxBackupsToKeep)
            {
                // 若数量未超上限，则直接累加计数
                backupIndex = maxIndex + 1;
            }
            else
            {
                // 若数量超过上限，将最早的一个删掉并且平移所有备份
                Array.Sort(backupFiles, StringComparer.InvariantCulture);
                try
                {
                    File.Delete(backupFiles[0]);
                }
                catch (Exception ex)
                {
                    Main.Logger.Error($"{ex.Message}\n{ex.StackTrace}\n{ex.Source}");
                }

                for (int i = 1; i < backupFiles.Length; i++)
                {
                    try
                    {
                        File.Move(backupFiles[i], backupFiles[i - 1]);
                    }
                    catch (Exception ex)
                    {
                        Main.Logger.Error($"{ex.Message}\n{ex.StackTrace}\n{ex.Source}");
                    }
                }

                backupIndex = Main.settings.maxBackupsToKeep - 1;
            }

            // 保存备份
            var targetFile = Path.Combine(BackPath, $"Date_{DateId}.save.{backupIndex:D3}.zip");
            Main.Logger.Log("备份路径:" + targetFile);
            BackupFolderToFile(SavePath, targetFile);
            Main.isBackuping = false;
            return targetFile;
        }

        /// <summary>
        /// 执行读档前备份
        /// </summary>
        private static string BackupBeforeLoad()
        {
            var targetFile = Path.Combine(BackPath, $"Date_{DateId}.load.zip");
            BackupFolderToFile(SavePath, targetFile);
            return targetFile;
        }

        /// <summary>
        /// 压缩到备份路径
        /// </summary>
        /// <param name="pathToBackup"></param>
        /// <param name="targetFile"></param>
        internal static void BackupFolderToFile(string pathToBackup, string targetFile)
        {
            var preDir = Environment.CurrentDirectory;
            Environment.CurrentDirectory = pathToBackup;
            var files = GetFilesToBackup();
            using (var zip = new ZipFile())
            {
                zip.AddFiles(files, true, "\\");
                zip.Save(targetFile);
            }
            Environment.CurrentDirectory = preDir;
        }

        /// <summary>
        /// 获取备份文件列表
        /// </summary>
        /// <returns></returns>
        internal static List<string> GetFilesToBackup()
        {
            var files = new List<string>();
            if (File.Exists(Path.Combine(".", "date.json")))
            {
                files.Add(Path.Combine(".", "date.json"));
            }

            files.AddRange(Directory.GetFiles(
                ".",
                "*.tw*",
                SearchOption.AllDirectories));
            return files;
        }

        /// <summary>
        /// 返回存档备份存储路径
        /// </summary>
        /// <param name="pathToBackup"></param>
        /// <returns></returns>
        internal static string GetBackupStoragePath(string pathToBackup)
        {
            string path = Path.Combine(Directory.GetParent(pathToBackup).FullName, "SaveBackup");

            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            return path;
        }
    }

    /// <summary>
    /// 按钮的提示信息
    /// </summary>
    [HarmonyPatch(typeof(WindowManage), "WindowSwitch")]
    public static class WindowManage_WindowSwitch_Patch
    {
        [HarmonyBefore(new[] { "CharacterFloatInfo" })]
        public static void Postfix(GameObject tips,
            ref Text ___informationMassage, ref Text ___informationName,
            ref int ___tipsW, ref bool ___anTips)
        {
            if (!Main.Enabled || tips == null) return;
            if (tips.name == "SaveButton")
            {
                ___informationName.text = "立即储存";
                ___informationMassage.text = "立即储存当前进度\n";
                ___tipsW = 230;
                ___anTips = true;
            }
            else if (tips.name == "LoadButtonList")
            {
                ___informationName.text = "载入";
                ___informationMassage.text = "显示存档列表，选择存档读取\n";
                ___tipsW = 260;
                ___anTips = true;
            }
            else if (tips.name == "LoadButton")
            {
                ___informationName.text = "快速载入";
                ___informationMassage.text = "放弃当前进度, 重新读档\n";
                ___tipsW = 260;
                ___anTips = true;
            }
        }
    }

    /// <summary>
    /// 判断是否需要存档，同时写入date.json
    /// </summary>
    [HarmonyPatch(typeof(SaveDateFile), "LateUpdate")]
    public class SaveDateFile_LateUpdate_Patch
    {
        private static bool Prefix(SaveDateFile __instance, ref bool ___saveSaveDateOK1, ref bool ___saveSaveDateOK2, ref bool ___saveSaveDateOK3)
        {
            if (!Main.Enabled || UIDate.instance == null) return true;

            if (__instance.saveSaveDate)
            {
                if (Main.ForceSave)
                {
                    Main.ForceSave = false;
                    UIDate.instance.trunSaveText.text = "手动存档";
                }
                else if (Main.settings.blockAutoSave)
                {
                    UIDate.instance.trunSaveText.text = "由于您的MOD设置，游戏未保存";
                    __instance.saveSaveDate = false;
                    return true;
                }
                // 写入date.json
                WriteSaveSummary();
            }
            if (___saveSaveDateOK1 && ___saveSaveDateOK2 && ___saveSaveDateOK3)
            {
                ___saveSaveDateOK1 = false;
                ___saveSaveDateOK2 = false;
                ___saveSaveDateOK3 = false;

                // Application.dataPath只能在主线程里使用
                dirInfo = new DirectoryInfo(
                    Path.Combine(Application.dataPath,
                    "SaveFiles" + SaveDateFile.instance.GetSaveFilesSuffix(),
                    "Backup",
                    $"Date_{SaveManager.DateId}"));

                Task.Run(new Action(EnsureFiles));

                return false;
            }
            return true;
        }
        private static DirectoryInfo dirInfo;
        /// <summary>
        /// 替换原本的SaveDateFile.EnsureFiles
        /// </summary>
        /// <returns></returns>
        private static void EnsureFiles()
        {
            string[] fileNames = new string[9]
            {
                SaveDateFile.instance.GameSettingName,
                SaveDateFile.instance.WorldDateName2,
                SaveDateFile.instance.WorldDateName4,
                SaveDateFile.instance.saveDateName,
                SaveDateFile.instance.homeBuildingName,
                SaveDateFile.instance.WorldDateName3,
                SaveDateFile.instance.PlaceResourceName,
                SaveDateFile.instance.actorLifeName,
                SaveDateFile.instance.legendBookName
            };
            string path = SaveManager.SavePath;
            int num;
            for (int i = 0; i < fileNames.Length; i = num)
            {
                string tmpFile = $"{path}{fileNames[i]}{SaveDateFile.instance.saveVersionName}~";
                string dstFile = $"{path}{fileNames[i]}{SaveDateFile.instance.saveVersionName}";
                if (!File.Exists(tmpFile))
                {
                    Debug.Log("存档异常");
                    break;
                }
                if (File.Exists(dstFile))
                {
                    File.Replace(tmpFile, dstFile, null);
                }
                else
                {
                    File.Move(tmpFile, dstFile);
                }
                num = i + 1;
            }
            Debug.Log("完成保存存档操作,开始执行随档备份...");

            string file = SaveManager.Backup(SaveManager.AfterSaveBackup);

            DoDefaultBackup(SaveManager.DateId, file);
        }
        private static bool DoDefaultBackup(int dateId, string file)
        {
            if (!SaveManager.Backuper.IsOn)
            {
                Debug.Log("过时节备份已禁用!");
                return false;
            }
            if (SaveManager.autoBackupRemain > 1)
            {
                SaveManager.autoBackupRemain--;
                Debug.Log($"未到设定的自动备份时间,等待时节剩余:{SaveManager.autoBackupRemain}");
                return false;
            }
            if (!SaveManager.SavePath.CheckDirectory())
            {
                Debug.Log("当前存档位置不存在!");
                return false;
            }
            if (!SaveManager.Backuper.CheckAllSaveFileExist(dateId))
            {
                Debug.Log("当前存档不完整,不进行备份操作!");
                return false;
            }
            if (!dirInfo.Exists)
            {
                dirInfo.Create();
            }
            var array = ReflectionMethod.Invoke<SaveDateBackuper, byte[]>(SaveManager.Backuper, "BackupItemToBytes");
            var path = Path.Combine(
                dirInfo.FullName,
                DateTime.Now.ToString("yyyyMMddHHmmss")
                    + array.Length
                    + "."
                    + ReflectionMethod.GetValue<SaveDateBackuper, string>(SaveManager.Backuper, "_extension"));
            using (var fs = new FileStream(path, FileMode.OpenOrCreate))
            {
                Debug.Log("写入备份描述!");
                fs.Write(array, 0, array.Length);
                if (File.Exists(file))
                {
                    var buff = File.ReadAllBytes(file);
                    fs.Write(buff, 0, buff.Length);
                }
                else
                {
                    using (var zip = new ZipFile())
                    {
                        zip.AddDirectory(SaveManager.SavePath);
                        zip.Save(fs);
                    }
                }
                Debug.Log($"Date_{dateId}备份完成!");
            }
            SaveManager.autoBackupRemain = SaveManager.Backuper.TickInterval;
            ReflectionMethod.Invoke(SaveManager.Backuper, "CheckBackupCount", dateId);
            return true;
        }

        /// <summary>
        /// 保存当前摘要到date.json
        /// </summary>
        private static void WriteSaveSummary()
        {
            var df = DateFile.instance;
            var data = new SaveData(
                df.GetActorName(0, false, false),
                df.year,
                df.samsara,
                df.dayTrun,
                df.playerSeatName,
                DateTime.Now);

            File.WriteAllText(
                Path.Combine(SaveManager.SavePath, "date.json"),
                JsonConvert.SerializeObject(data));
        }
    }
    [HarmonyPatch(typeof(SaveDateBackuper), "ExtractBackupToTemp")]
    public class SaveDateBackuper_ExtractBackupToTemp_Patch
    {
        private static bool Prefix(SaveDateBackuper __instance, ref BackupItem item, ref string __result)
        {
            if (!Main.Enabled || item == null) return true;
            var saveDateFile = SaveDateFile.instance;
            var dir = Path.Combine(
                saveDateFile.datePath,
                "SaveFiles" + saveDateFile.GetSaveFilesSuffix(),
                "Backup", $"Date_{item.DataId}",
                Path.GetFileNameWithoutExtension(item.fileName));
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
            Directory.CreateDirectory(dir);
            Main.Logger.Log("Unzip file: " + item.fileName);
            using (var fs = File.OpenRead(item.fileName))
            {
                var name = item.fileName.Split('\\', '/').Last();
                fs.Seek(int.TryParse(name.Substring(14, name.LastIndexOf('.') - 14), out var num) ? num : 512, SeekOrigin.Begin);
                Main.Logger.Log($"skip:{num}");
                using (var zip = ZipFile.Read(fs))
                {
                    zip.ExtractAll(dir, ExtractExistingFileAction.OverwriteSilently);
                }
            }
            return false;
        }
    }
}