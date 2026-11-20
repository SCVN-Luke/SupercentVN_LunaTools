using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Supercent.Util.Editor
{
    // Screen captures of replays (luna_replay with captureEvery): the page grabs a frame every N seconds of play, this
    // writes them to <project>/Library/LunaDevTools/Captures/<recording> <time>/ with a frame list, contact sheets and a
    // player page (index.html: plays the frames at their play times, scrub, step, speed). Library keeps them out of Git
    // and out of the Luna build. No Unity API: the proxy calls it from its own threads.
    public static class LunaCaptures
    {
        public static string Root => Path.Combine(Directory.GetCurrentDirectory(), "Library", "LunaDevTools", "Captures");

        public static string NewFolder(string recording)
        {
            string safe = LunaRecordings.FileName(recording);
            safe = safe.Substring(0, safe.Length - ".json".Length);
            string dir = Path.Combine(Root, safe + " " + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static string FrameFile(int index, double seconds) =>
            string.Format(CultureInfo.InvariantCulture, "f{0:0000}_{1:000.000}s.jpg", index, seconds);

        public static void WriteJpeg(string path, string base64) => File.WriteAllBytes(path, Convert.FromBase64String(base64));

        // frames: {file, seconds, tick}. Writes frames.json and index.html.
        public static string WritePlayer(string dir, string recording, double every, List<Dictionary<string, object>> frames)
        {
            File.WriteAllText(Path.Combine(dir, "frames.json"), MiniJson.Write(new Dictionary<string, object>
            {
                { "recording", recording }, { "every", every }, { "frames", frames },
            }));
            var list = new StringBuilder();
            foreach (var f in frames)
                list.Append(string.Format(CultureInfo.InvariantCulture, "[\"{0}\",{1}],", f["file"], f["seconds"]));
            string html = Player.Replace("__TITLE__", System.Net.WebUtility.HtmlEncode(recording))
                                .Replace("__FRAMES__", "[" + list.ToString().TrimEnd(',') + "]");
            string path = Path.Combine(dir, "index.html");
            File.WriteAllText(path, html);
            return path;
        }

        const string Player = @"<!doctype html><html><head><meta charset=""utf-8""><title>__TITLE__</title>
<style>body{margin:0;background:#111;color:#ddd;font:13px sans-serif;display:flex;flex-direction:column;align-items:center}
#v{max-height:calc(100vh - 70px);max-width:100vw;image-rendering:auto}#bar{display:flex;gap:8px;align-items:center;padding:8px;width:min(720px,96vw)}
#s{flex:1}button,select{background:#333;color:#ddd;border:1px solid #555;border-radius:4px;padding:3px 8px}#t{min-width:110px;font-variant-numeric:tabular-nums}</style></head>
<body><img id=""v""><div id=""bar""><button id=""p"">Play</button><input id=""s"" type=""range"" min=""0"" value=""0""><span id=""t""></span>
<select id=""r""><option>0.1</option><option>0.25</option><option>0.5</option><option selected>1</option><option>2</option></select></div>
<div style=""opacity:.6"">Space play/pause, arrows step a frame, __TITLE__</div>
<script>var F=__FRAMES__,i=0,on=false,t0=0,s0=0,v=document.getElementById('v'),s=document.getElementById('s'),t=document.getElementById('t'),p=document.getElementById('p'),r=document.getElementById('r');
F.forEach(function(f){new Image().src=f[0];});s.max=F.length-1;
function show(k){i=Math.max(0,Math.min(F.length-1,k));v.src=F[i][0];s.value=i;t.textContent=F[i][1].toFixed(3)+' s  ('+(i+1)+'/'+F.length+')';}
function loop(now){if(!on)return;var e=s0+(now-t0)/1000*+r.value;while(i<F.length-1&&F[i+1][1]<=e)i++;show(i);if(i>=F.length-1){on=false;p.textContent='Play';return;}requestAnimationFrame(loop);}
function play(){if(i>=F.length-1)i=0;on=!on;p.textContent=on?'Pause':'Play';if(on){t0=performance.now();s0=F[i][1];requestAnimationFrame(loop);}}
p.onclick=play;s.oninput=function(){on=false;p.textContent='Play';show(+s.value);};r.onchange=function(){if(on){t0=performance.now();s0=F[i][1];}};
document.onkeydown=function(e){if(e.key===' '){e.preventDefault();play();}else if(e.key==='ArrowRight'){on=false;show(i+1);}else if(e.key==='ArrowLeft'){on=false;show(i-1);}};show(0);</script></body></html>";
    }
}
