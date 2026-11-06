using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Drawing;
using System.Diagnostics;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// Portable UI only: no process memory access, injection, or game launcher.
public sealed class PBZSuiteHost : Form {
 const string Reforge="combat_reforge";
 const string Pressure="pressure";
 static readonly string[] ReforgeMembers={
  "guard_balance","normal_guard_cost","break_duration","parry_recovery",
  "guard_gate","damage_softcap","enemy_attack"
 };
 [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
 [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
 readonly string dir=AppDomain.CurrentDomain.BaseDirectory;
 readonly JavaScriptSerializer json=new JavaScriptSerializer();
 readonly Dictionary<string,CheckBox> boxes=new Dictionary<string,CheckBox>();
 readonly Dictionary<string,Label> states=new Dictionary<string,Label>();
 readonly Dictionary<string,Label> descriptions=new Dictionary<string,Label>();
 readonly Dictionary<string,bool> settings=new Dictionary<string,bool>();
 static readonly string[,] modules={
  {Reforge,"战斗重铸","重新平衡了战斗系统","Combat Reforged","Rebalances the combat system"},
  {"perilous_audio","危音效","红光与黑光预警音","Perilous Attack Audio","Audio cues for red and black flashes"},
  {"hitstop","打击感","命中与精防额外动画停顿","Hitstop","Extra hitstop on hits and perfect deflects"},
  {"executions","全武器处决","同武器大类共享处决池","Weapon Executions","Shared executions within each weapon family"},
  {Pressure,"BOSS AI 强化","重做了boss 战斗 ai","Boss AI Overhaul","Reworks boss combat AI"}
 };
 static readonly Dictionary<string,string> EnglishStatus=new Dictionary<string,string>{
  {"已开启","Enabled"},{"已关闭","Disabled"},{"部分开启","Partly enabled"},
  {"正在切换","Applying changes"},{"加载失败","Load failed"},{"运行失败","Runtime error"},
  {"待适配","Unsupported"},{"等待角色加载","Waiting for character"},{"等待游戏加载","Waiting for game"},
  {"等待音频进程","Waiting for audio"},{"等待 Boss 战斗","Waiting for boss fight"},
  {"压力尚未确认","Pressure unconfirmed"},{"游戏未运行","Game not running"},
  {"等待游戏回报","Waiting for game"},{"等待模块","Waiting for module"},
  {"等待游戏确认…","Applying changes…"}
 };
 readonly Label heading=new Label(),instruction=new Label(),footer=new Label();
 readonly RadioButton chineseLanguage=new RadioButton(),englishLanguage=new RadioButton();
 readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
 Process game; bool english,updating,previousF6,previousF8,sawGame,inputPrimed;
 int ticks; PerilousAudioHost audio; string audioError="",saveError="";
 readonly PressureToast toast=new PressureToast();DateTime toastUntil;
 readonly HashSet<string> cues=new HashSet<string>();readonly ToolTip tips=new ToolTip();
 public PBZSuiteHost() {
  Font=new Font("Microsoft YaHei UI",15);
  ClientSize=new Size(980,475); MinimumSize=Size; StartPosition=FormStartPosition.CenterScreen;
  BackColor=Color.FromArgb(25,27,33); ForeColor=Color.FromArgb(236,237,241); TopMost=true;
  heading.Font=new Font(Font.FontFamily,31.5f,FontStyle.Bold);
  heading.Location=new Point(24,16);heading.AutoSize=true;Controls.Add(heading);
  var language=new Panel{Location=new Point(752,23),Size=new Size(204,40),Anchor=AnchorStyles.Top|AnchorStyles.Right,AccessibleName="语言 / Language"};
  AddLanguageButton(language,chineseLanguage,"中文",0,false);
  AddLanguageButton(language,englishLanguage,"English",104,true);
  Controls.Add(language);
  instruction.Location=new Point(26,77);instruction.AutoSize=true;Controls.Add(instruction);
  var panel=new TableLayoutPanel{Location=new Point(20,118),Size=new Size(940,270),ColumnCount=3,RowCount=modules.GetLength(0),Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right|AnchorStyles.Bottom};
  panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,30));panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,46));panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,24));
  foreach(string id in ReforgeMembers)settings[id]=true;
  for(int i=0;i<modules.GetLength(0);i++){
   panel.RowStyles.Add(new RowStyle(SizeType.Percent,100f/modules.GetLength(0)));
   string id=modules[i,0];
   if(id!=Reforge)settings[id]=id!=Pressure;
   var box=new CheckBox{Dock=DockStyle.Fill,AutoSize=true};
   if(id==Reforge){
    box.AutoCheck=false;
    box.Click+=(s,e)=>{
     bool enable=box.CheckState!=CheckState.Checked;
     foreach(string member in ReforgeMembers)settings[member]=enable;
     SyncCheckboxes();Save();SetStatus(Reforge,"等待游戏确认…");
    };
   }else{
    box.CheckedChanged+=(s,e)=>{
     if(updating)return;
     settings[id]=box.Checked;Save();SetStatus(id,"等待游戏确认…");
     if(id==Pressure)ShowPressureToast();
    };
   }
   boxes[id]=box;panel.Controls.Add(box,0,i);
   var description=new Label{Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,ForeColor=Color.FromArgb(171,179,194),Font=new Font(Font.FontFamily,13.5f)};
   descriptions[id]=description;panel.Controls.Add(description,1,i);
   var status=new Label{Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft};
   states[id]=status;panel.Controls.Add(status,2,i);
  }
  Controls.Add(panel);footer.Location=new Point(24,404);footer.Size=new Size(932,60);footer.Anchor=AnchorStyles.Bottom|AnchorStyles.Left|AnchorStyles.Right;footer.ForeColor=Color.FromArgb(171,179,194);Controls.Add(footer);
  FormClosing+=(s,e)=>{if(e.CloseReason==CloseReason.UserClosing){e.Cancel=true;Hide();}};
  LoadSettings();ApplyLanguage();RefreshStatus();
  System.Threading.ThreadPool.QueueUserWorkItem(_=>AudioLoop());
  timer.Interval=50;timer.Tick+=(s,e)=>Poll();timer.Start();Shown+=(s,e)=>Hide();
 }
 void AddLanguageButton(Panel panel,RadioButton button,string label,int left,bool useEnglish){
  button.Text=label;button.Appearance=Appearance.Button;button.TextAlign=ContentAlignment.MiddleCenter;
  button.SetBounds(left,0,100,40);button.FlatStyle=FlatStyle.Flat;
  button.FlatAppearance.BorderColor=Color.FromArgb(92,103,120);
  button.FlatAppearance.CheckedBackColor=Color.FromArgb(53,85,121);
  button.CheckedChanged+=(s,e)=>{
   if(updating||!button.Checked||english==useEnglish)return;
   english=useEnglish;Save();ApplyLanguage();RefreshStatus();
  };
  panel.Controls.Add(button);
 }
 Dictionary<string,object> Read(string file){try{using(var stream=new FileStream(Path.Combine(dir,file),FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))using(var reader=new StreamReader(stream,Encoding.UTF8)){return json.Deserialize<Dictionary<string,object>>(reader.ReadToEnd());}}catch{return null;}}
 string Local(string zh,string en){return english?en:zh;}
 string StatusText(string status){
  string translation;
  return english&&EnglishStatus.TryGetValue(status,out translation)?translation:status;
 }
 void ApplyLanguage(){
  Text=heading.Text=Local("MIE-影之刃零 战斗重铸","MIE - Combat Reforged");
  instruction.Text=Local("勾选开启，取消关闭。右侧显示游戏实际回报的状态。","Toggle a feature with its checkbox. The game reports its status on the right.");
  for(int i=0;i<modules.GetLength(0);i++){
   string id=modules[i,0];
   boxes[id].Text=Local(modules[i,1],modules[i,3]);
   descriptions[id].Text=Local(modules[i,2],modules[i,4]);
  }
  tips.SetToolTip(boxes[Reforge],Local("切换后，重新载入战斗可更新已生成敌人的攻击力。","Reload the fight after toggling to update enemies that have already spawned."));
 }
 void SyncCheckboxes(){
  updating=true;
  chineseLanguage.Checked=!english;
  englishLanguage.Checked=english;
  int enabled=ReforgeMembers.Count(id=>settings[id]);
  boxes[Reforge].CheckState=enabled==0?CheckState.Unchecked:enabled==ReforgeMembers.Length?CheckState.Checked:CheckState.Indeterminate;
  foreach(var id in boxes.Keys)if(id!=Reforge)boxes[id].Checked=settings[id];
  updating=false;
 }
 void LoadSettings(){
  var obj=Read("Settings.json");
  if(obj==null){
   SyncCheckboxes();
   if(!File.Exists(Path.Combine(dir,"Settings.json")))Save();
   return;
  }
  foreach(var id in settings.Keys.ToArray()){
   object value;
   if(obj.TryGetValue(id,out value)&&value is bool)settings[id]=(bool)value;
  }
  object savedLanguage;
  bool useEnglish=obj.TryGetValue("language",out savedLanguage)&&Equals(savedLanguage,"en");
  bool changed=english!=useEnglish;
  english=useEnglish;SyncCheckboxes();
  if(changed)ApplyLanguage();
 }
 void Save(){
  try{
   string path=Path.Combine(dir,"Settings.json"),temp=path+".tmp";
   var values=settings.ToDictionary(pair=>pair.Key,pair=>(object)pair.Value);
   values["language"]=english?"en":"zh";
   File.WriteAllText(temp,json.Serialize(values),new UTF8Encoding(false));
   if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
   saveError="";
  }catch(Exception e){saveError=e.Message;}
 }
 static Dictionary<string,object> StatusRow(Dictionary<string,object> rows,string id){
  object row;
  return rows!=null&&rows.TryGetValue(id,out row)?row as Dictionary<string,object>:null;
 }
 static string Field(Dictionary<string,object> row,string field,string fallback=""){
  object value;
  return row!=null&&row.TryGetValue(field,out value)?Convert.ToString(value):fallback;
 }
 static bool Failed(string status){return status.Contains("失败")||status=="待适配"||status.Contains("不支持");}
 void SetStatus(string id,string status,string detail=""){
  states[id].Text=StatusText(status);
  states[id].ForeColor=status=="已开启"?Color.LightGreen:Failed(status)?Color.Salmon:Color.LightGray;
  tips.SetToolTip(states[id],detail);
 }
 void RefreshStatus(){
  var obj=Read("Status.json");
  long time;
  bool live=long.TryParse(Field(obj,"time"),out time)&&DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()-time<5000;
  var rows=StatusRow(obj,"modules");
  string fallback=live?"等待模块":game==null?"游戏未运行":"等待游戏回报";
  foreach(var id in states.Keys){
   if(!live){SetStatus(id,fallback);continue;}
   if(id==Reforge){
    var reports=ReforgeMembers.Select(member=>Field(StatusRow(rows,member),"status","等待模块")).ToArray();
    string status=reports.FirstOrDefault(Failed);
    if(status==null){
     if(reports.All(report=>report==reports[0]))status=reports[0];
     else if(reports.All(report=>report=="已开启"||report=="已关闭"))status="部分开启";
     else status="等待游戏确认…";
    }
    string detail=string.Join("\n",ReforgeMembers.Select((member,index)=>
     member+": "+StatusText(reports[index])+(Failed(reports[index])?" — "+Field(StatusRow(rows,member),"detail"):"")));
    SetStatus(id,status,detail);
   }else{
    var row=StatusRow(rows,id);
    string status=Field(row,"status",fallback);
    SetStatus(id,status,!english||Failed(status)?Field(row,"detail"):"");
   }
  }
  footer.Text=Local("F6 显示／隐藏窗口    ·    F8 切换 BOSS AI 强化","F6 Show / hide window    ·    F8 Toggle Boss AI Overhaul")+"\n"+
   (saveError!=""?Local("保存失败","Save failed"):live?Local("已连接游戏，设置会自动保存。","Connected. Settings are saved automatically."):Local("等待游戏回报。","Waiting for the game to report its status."));
  tips.SetToolTip(footer,saveError);
  if(toast.Visible)UpdatePressureToast(states[Pressure].Text);
 }
 void Poll(){
  uint fg;GetWindowThreadProcessId(GetForegroundWindow(),out fg);
  if(++ticks%20==1){
   LoadSettings();
   if(game!=null&&game.HasExited&&sawGame){Application.Exit();return;}
   if(game==null){string expected=Path.GetFullPath(Path.Combine(dir,"../../../PBZ.exe"));foreach(var p in Process.GetProcessesByName("PBZ")){try{if(String.Equals(p.MainModule.FileName,expected,StringComparison.OrdinalIgnoreCase)){game=p;sawGame=true;break;}}catch{}}}
   RefreshStatus();
   try{File.WriteAllText(Path.Combine(dir,"HostStatus.json"),json.Serialize(new{audioReady=audio!=null,audioError=audioError,gamePid=game==null?0:game.Id,time=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}),new UTF8Encoding(false));}catch{}
  }
  bool allowed=(game!=null&&fg==(uint)game.Id)||fg==(uint)Process.GetCurrentProcess().Id;
  short raw6=GetAsyncKeyState(0x75),raw8=GetAsyncKeyState(0x77);bool f6=(raw6&0x8000)!=0,f8=(raw8&0x8000)!=0;
  if(inputPrimed&&allowed&&((raw6&1)!=0||(f6&&!previousF6))){if(Visible)Hide();else{Show();Activate();}}
  if(inputPrimed&&allowed&&((raw8&1)!=0||(f8&&!previousF8))){settings[Pressure]=!settings[Pressure];SyncCheckboxes();Save();ShowPressureToast();}
  inputPrimed=true;previousF6=f6;previousF8=f8;
  if(toast.Visible&&DateTime.UtcNow>=toastUntil)toast.Hide();
  if(game!=null&&game.HasExited)Application.Exit();
 }
 void UpdatePressureToast(string status){
  toast.SetMessage(Local("BOSS AI 强化","Boss AI Overhaul")+" · "+status+(status==StatusText("已开启")?" (100)":""));
 }
 void ShowPressureToast(){
  toastUntil=DateTime.UtcNow.AddSeconds(10);
  UpdatePressureToast(settings[Pressure]?Local("正在开启…","Enabling…"):Local("正在关闭…","Disabling…"));
  var area=Screen.FromPoint(Cursor.Position).WorkingArea;
  toast.Location=new Point(area.Left+32,area.Top+80);toast.Show();
 }
 sealed class PressureToast:Form{
  readonly Label label=new Label();public PressureToast(){FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;Size=new Size(520,72);StartPosition=FormStartPosition.Manual;BackColor=Color.FromArgb(31,35,42);label.Dock=DockStyle.Fill;label.TextAlign=ContentAlignment.MiddleCenter;label.Font=new Font("Microsoft YaHei UI",16.5f);label.ForeColor=Color.White;Controls.Add(label);}
  protected override bool ShowWithoutActivation{get{return true;}}
  protected override CreateParams CreateParams{get{var p=base.CreateParams;p.ExStyle|=0x08000080;return p;}}
  public void SetMessage(string s){label.Text=s;}
 }
 void AudioLoop(){
  // WASAPI uses COM MTA; the manager window runs on STA.
  try{audio=new PerilousAudioHost(Path.Combine(dir,"Audio/perilous.wav"));}catch(Exception e){audioError=e.Message;}
  string folder=Path.Combine(dir,"Cues");Directory.CreateDirectory(folder);
  while(!IsDisposed){try{foreach(string path in Directory.GetFiles(folder,"*.cue")){
   string id=Path.GetFileName(path);if(cues.Contains(id))continue;
   if((DateTime.UtcNow-File.GetLastWriteTimeUtc(path)).TotalSeconds>2){File.Delete(path);continue;}
   Dictionary<string,object> ev;try{ev=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(path,Encoding.UTF8));}catch{continue;}
   cues.Add(id);if(audio!=null){audio.Play(Convert.ToString(ev["id"]),DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());}
   File.Delete(path);
  }}catch{}System.Threading.Thread.Sleep(5);}
 }
 [STAThread] public static void Main(string[] args){
  bool created;using(var mutex=new System.Threading.Mutex(true,"Local\\PBZSuiteManager-26367565",out created)){if(!created)return;Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Application.Run(new PBZSuiteHost());}
 }
}
