using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Numerics;
using Shikari.Model;
namespace Dalamud.Bindings.ImGui
{
    public enum ImDrawFlags { None }
    public sealed class ImDrawListPtr
    {
        public Graphics G;
        public ImDrawListPtr(Graphics g) { G = g; }
        private static Color C(uint c) => Color.FromArgb((int)(c >> 24), (int)(c & 255), (int)((c >> 8) & 255), (int)((c >> 16) & 255));
        private static PointF P(Vector2 p) => new(p.X,p.Y);
        public void AddCircleFilled(Vector2 p,float r,uint c,int segments) { using var b = new SolidBrush(C(c)); G.FillEllipse(b,p.X-r,p.Y-r,r*2,r*2); }
        public void AddCircle(Vector2 p,float r,uint c,int segments,float width) { using var pen = new Pen(C(c),width); G.DrawEllipse(pen,p.X-r,p.Y-r,r*2,r*2); }
        public void AddLine(Vector2 a,Vector2 b,uint c,float width) { using var pen = new Pen(C(c),width); G.DrawLine(pen,P(a),P(b)); }
        public void AddTriangleFilled(Vector2 a,Vector2 b,Vector2 d,uint c) => Fill(new[]{P(a),P(b),P(d)},c);
        public void AddQuadFilled(Vector2 a,Vector2 b,Vector2 d,Vector2 e,uint c) => Fill(new[]{P(a),P(b),P(d),P(e)},c);
        private void Fill(PointF[] points,uint c) { using var b = new SolidBrush(C(c)); G.FillPolygon(b,points); }
        private static GraphicsPath Rounded(Vector2 min,Vector2 max,float r)
        {
            var p = new GraphicsPath(); var d = Math.Max(1,r*2);
            p.AddArc(min.X,min.Y,d,d,180,90); p.AddArc(max.X-d,min.Y,d,d,270,90);
            p.AddArc(max.X-d,max.Y-d,d,d,0,90); p.AddArc(min.X,max.Y-d,d,d,90,90); p.CloseFigure(); return p;
        }
        public void AddRectFilled(Vector2 min,Vector2 max,uint c,float rounding) { using var b=new SolidBrush(C(c)); using var p=Rounded(min,max,rounding); G.FillPath(b,p); }
        public void AddRect(Vector2 min,Vector2 max,uint c,float rounding,ImDrawFlags f,float width) { using var pen=new Pen(C(c),width); using var p=Rounded(min,max,rounding); G.DrawPath(pen,p); }
        public void Text(Vector2 at,string text,uint c) { using var b = new SolidBrush(C(c)); G.DrawString(text,Shikari.UI.UiHelpers.Font,b,P(at),StringFormat.GenericTypographic); }
    }
}
namespace Shikari.Services.Live
{
    public sealed class ArenaTracker { public readonly record struct LivePlayer(string Name,uint JobId,int SlotIndex,Vector2 Board,bool IsLocal); }
}
namespace Shikari.UI
{
    using Dalamud.Bindings.ImGui;
    using Shikari.Services.Live;
    public static class UiHelpers
    {
        public static float Scale => 1;
        public static Font Font = new("Segoe UI",12,FontStyle.Bold,GraphicsUnit.Pixel);
        public static Graphics G = null!;
        public static Vector2 TextSize(string text) { var s=G.MeasureString(text,Font,1000,StringFormat.GenericTypographic); return new(s.Width,s.Height); }
        public static void CenteredShadowText(ImDrawListPtr draw,Vector2 center,string text,uint color) => draw.Text(center-TextSize(text)/2,text,color);
    }
    public sealed partial class ArenaCanvas
    {
        private Vector2 origin;
        private float side;
        public int HighlightSlot { get; set; }
        public bool FocusOnMe { get; set; } = true;
        public bool LiveGuides { get; set; } = true;
        public bool Settled { get => miniSettled; set => miniSettled = value; }
        public float SettleDistance { get; set; }
        public float SettleTolerance { get; set; }
        public IReadOnlyList<ArenaTracker.LivePlayer>? LivePlayers { get; set; }
        private Vector2 ToScreen(Vector2 p) => origin+p*side;
        public static void CheckArrival()
        {
            var canvas = new ArenaCanvas { HighlightSlot = 0, SettleTolerance = 0.1f };
            var slide = new Slide();
            var target = new CanvasItem { Kind = CanvasItemKind.PlayerToken, SlotIndex = 0, Position = new(0.5f, 0.5f) };
            slide.Items.Add(target);
            void Move(float x) { canvas.LivePlayers = new[] { new ArenaTracker.LivePlayer("You", 0, 0, new Vector2(x,0.5f), true) }; canvas.SettleDistance = -1; canvas.MeasureMiniSettle(slide); }
            Move(0.59f); if (!canvas.Settled) throw new Exception("Arrival failed");
            Move(0.605f); if (!canvas.Settled) throw new Exception("Boundary hysteresis failed");
            Move(0.62f); if (canvas.Settled) throw new Exception("Leaving destination failed");
            Move(0.5f); target.Position = new(0.7f,0.5f); Move(0.595f); if (canvas.Settled) throw new Exception("Moved target inherited arrival");
            target.Position = new(0.5f,0.5f); Move(0.5f);
            canvas.LivePlayers = null; canvas.SettleDistance = -1; canvas.MeasureMiniSettle(slide);
            if (canvas.Settled || canvas.SettleDistance != -1) throw new Exception("Missing alignment retained arrival");
            Move(0.5f); slide.Items.Add(new CanvasItem { Kind=CanvasItemKind.PlayerToken,SlotIndex=0 }); Move(0.5f);
            if (canvas.Settled) throw new Exception("Ambiguous target claimed arrival");
            Console.WriteLine("PASS: production mini arrival, boundary hysteresis, movement, edited target, lost alignment and ambiguous destination");
        }        public static void Render(string path)
        {
            using var bitmap = new Bitmap(1030,480);
            using var g = Graphics.FromImage(bitmap);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.FromArgb(8,8,11)); UiHelpers.G=g;
            var draw=new ImDrawListPtr(g);
            var plan=PlanDocument.CreateDefault(); var slide=plan.Slides[0]; slide.Items.Clear();
            var labels=new[]{"T1","T2","H1","H2","M1","M2","R1","R2"};
            for(var i=0;i<8;i++) slide.Items.Add(new CanvasItem {Kind=CanvasItemKind.PlayerToken,SlotIndex=i,Text=labels[i],Position=new(0.20f+(i%2)*0.025f,0.26f+(i/2)*0.038f),Color=0xFFECAC65});
            slide.Items.Add(new CanvasItem {Kind=CanvasItemKind.EnemyToken,Layer=99,Position=new(0.5f,0.23f),Radius=0.34f,Color=0xFF0000ED});
            for(var state=0;state<3;state++)
            {
                var canvas=new ArenaCanvas {origin=new(15+state*340,46),side=320,HighlightSlot=4,Settled=state==2};
                if(state>0) canvas.LivePlayers=new[]{new ArenaTracker.LivePlayer("You",0,4,state==2 ? slide.Items[4].Position : new Vector2(0.61f,0.74f),true)};
                draw.Text(new(15+state*340,16),new[]{"PLAN ONLY / NO ALIGNMENT","LIVE / MOVING TO YOUR SPOT","LIVE / IN POSITION"}[state],0xFFFFFFFF);
                draw.AddRectFilled(canvas.origin,canvas.origin+new Vector2(320),0xFF171416,6);
                for(var n=1;n<8;n++) {draw.AddLine(canvas.origin+new Vector2(n*40,0),canvas.origin+new Vector2(n*40,320),0x16FFFFFF,1);draw.AddLine(canvas.origin+new Vector2(0,n*40),canvas.origin+new Vector2(320,n*40),0x16FFFFFF,1);}
                var clip = g.Save(); g.SetClip(new RectangleF(canvas.origin.X,canvas.origin.Y,320,320));
                foreach(var item in slide.Items.OrderBy(MiniMapLayout.Layer))
                {
                    if(item.Kind==CanvasItemKind.Zone) {draw.AddCircleFilled(canvas.ToScreen(item.Position),item.Radius*320,MiniMapLayout.HazardFill(item.Color),64);draw.AddCircle(canvas.ToScreen(item.Position),item.Radius*320,0xE60000ED,64,2);}
                    else if(item.Kind==CanvasItemKind.EnemyToken) canvas.DrawMiniEnemy(draw,item,canvas.ToScreen(item.Position));
                    else canvas.DrawMiniToken(draw,plan,item,canvas.ToScreen(item.Position));
                }
                canvas.DrawMiniLivePlayers(draw,plan,slide);canvas.DrawMiniDestination(draw,slide);canvas.DrawMiniLabels(draw);
                g.Restore(clip);
                draw.AddRect(canvas.origin,canvas.origin+new Vector2(320),0xFF555555,6,ImDrawFlags.None,1);
                draw.Text(canvas.origin+new Vector2(10,333),new[]{"PLAN VIEW — place matching waymarks","White diamond = you. Cyan ring = your spot.","IN POSITION — within your set tolerance"}[state],0xFFFFFFFF);
                draw.Text(canvas.origin+new Vector2(10,358),"Grotesquerie: Act 1",0xFFD0CCCC);
                draw.Text(canvas.origin+new Vector2(10,379),"One role gets stack. One role gets spread.",0xFFD0CCCC);
            }
            bitmap.Save(path,ImageFormat.Png);
        }
    }
}
