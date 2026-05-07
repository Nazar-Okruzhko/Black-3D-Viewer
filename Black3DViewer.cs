using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using System.Xml;
using OpenTK;
using OpenTK.Graphics;
using OpenTK.Graphics.OpenGL;
 
namespace Viewer3D
{
 
// =============================================================================
//  SECTION 1 - SHADER SOURCES
//  Lower ambient (0.12) gives "dark-white with visible depth" on solid models.
//  Dark background (0.18 grey) makes shading contrast pop.
// =============================================================================
 
    public static class ShaderSource
    {
        public const string Vert = @"
#version 330 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aTexCoord;
layout(location = 3) in vec3 aTangent;
 
out vec3 FragPos;
out vec3 Normal;
out vec2 TexCoord;
out mat3 TBN;
 
uniform mat4 model;
uniform mat4 view;
uniform mat4 projection;
 
void main()
{
    FragPos  = vec3(model * vec4(aPos, 1.0));
    Normal   = mat3(transpose(inverse(model))) * aNormal;
    TexCoord = aTexCoord;
 
    vec3 T = normalize(mat3(model) * aTangent);
    vec3 N = normalize(Normal);
    T = normalize(T - dot(T, N) * N);
    vec3 B = cross(N, T);
    TBN = mat3(T, B, N);
 
    gl_Position = projection * view * vec4(FragPos, 1.0);
}
";
        public const string Frag = @"
#version 330 core
out vec4 FragColor;
 
in vec3 FragPos;
in vec3 Normal;
in vec2 TexCoord;
in mat3 TBN;
 
uniform vec3 lightPos;
uniform vec3 lightPos2;
uniform vec3 viewPos;
 
uniform sampler2D colorMap;
uniform sampler2D normalMap;
uniform sampler2D specularMap;
uniform sampler2D roughnessMap;
uniform sampler2D metallicMap;
uniform sampler2D opacityMap;
 
uniform int hasColorMap;
uniform int hasNormalMap;
uniform int hasSpecularMap;
uniform int hasRoughnessMap;
uniform int hasMetallicMap;
uniform int hasOpacityMap;
 
uniform int  shadingMode;
uniform vec3 solidColor;
 
vec3 shade(vec3 lp, vec3 lc, vec3 norm, vec3 vd,
           vec3 albedo, float rough, float metal, float sstr)
{
    vec3  ld  = normalize(lp - FragPos);
    float d   = max(dot(norm, ld), 0.0);
    vec3  hv  = normalize(ld + vd);
    float s   = pow(max(dot(norm, hv), 0.0), (1.0 - rough) * 64.0 + 1.0);
    vec3  F0  = mix(vec3(0.04), albedo, metal);
    vec3  F   = F0 + (1.0 - F0) * pow(1.0 - max(dot(hv, vd), 0.0), 5.0);
    vec3  kD  = (1.0 - F) * (1.0 - metal);
    float dist = length(lp - FragPos);
    float att  = 1.0 / (1.0 + 0.001 * dist + 0.00005 * dist * dist);
    vec3 ambient  = 0.30 * lc * albedo;
    vec3 diffuse  = kD * d * lc * albedo * 1.4;
    vec3 specular = F * s * sstr * lc * 0.8;
    return ambient + (diffuse + specular) * att;
}
 
void main()
{
    if (shadingMode == 1) { FragColor = vec4(0.86, 0.86, 0.86, 1.0); return; }
 
    vec3 albedo = solidColor;
    if (hasColorMap != 0) albedo = texture(colorMap, TexCoord).rgb;
 
    vec3 norm = normalize(Normal);
    if (hasNormalMap != 0)
    {
        vec3 nt = texture(normalMap, TexCoord).rgb * 2.0 - 1.0;
        norm = normalize(TBN * nt);
    }
 
    float rough = (hasRoughnessMap != 0) ? texture(roughnessMap, TexCoord).r : 0.5;
    float metal = (hasMetallicMap  != 0) ? texture(metallicMap,  TexCoord).r : 0.0;
    float sstr  = (hasSpecularMap  != 0) ? texture(specularMap,  TexCoord).r : 0.4;
 
    vec3 vd     = normalize(viewPos - FragPos);
    vec3 result = shade(lightPos,  vec3(1.0, 1.0, 1.0), norm, vd, albedo, rough, metal, sstr)
                + shade(lightPos2, vec3(0.75, 0.75, 0.75), norm, vd, albedo, rough, metal, sstr);
 
    float alpha = (hasOpacityMap != 0) ? texture(opacityMap, TexCoord).r : 1.0;
    FragColor = vec4(result, alpha);
}
";
        public const string WireVert = @"
#version 330 core
layout(location = 0) in vec3 aPos;
uniform mat4 model;
uniform mat4 view;
uniform mat4 projection;
void main() { gl_Position = projection * view * model * vec4(aPos, 1.0); }
";
        public const string WireFrag = @"
#version 330 core
out vec4 FragColor;
uniform vec3 color;
void main() { FragColor = vec4(color, 1.0); }
";
    }
 
// =============================================================================
//  SECTION 2 - SHADER CLASS
// =============================================================================
 
    public class Shader
    {
        public int ProgramId;
        private readonly Dictionary<string, int> _cache = new Dictionary<string, int>();
 
        public Shader(string vert, string frag)
        {
            int v = Compile(ShaderType.VertexShader,   vert);
            int f = Compile(ShaderType.FragmentShader, frag);
            ProgramId = GL.CreateProgram();
            GL.AttachShader(ProgramId, v); GL.AttachShader(ProgramId, f);
            GL.LinkProgram(ProgramId);
            GL.GetProgram(ProgramId, GetProgramParameterName.LinkStatus, out int ok);
            if (ok == 0) throw new Exception("Shader link error:\n" + GL.GetProgramInfoLog(ProgramId));
            GL.DeleteShader(v); GL.DeleteShader(f);
        }
 
        private static int Compile(ShaderType t, string src)
        {
            int id = GL.CreateShader(t);
            GL.ShaderSource(id, src); GL.CompileShader(id);
            GL.GetShader(id, ShaderParameter.CompileStatus, out int ok);
            if (ok == 0) throw new Exception($"{t} error:\n" + GL.GetShaderInfoLog(id));
            return id;
        }
 
        public void Use() => GL.UseProgram(ProgramId);
 
        private int Loc(string n)
        {
            if (!_cache.TryGetValue(n, out int l))
                _cache[n] = l = GL.GetUniformLocation(ProgramId, n);
            return l;
        }
 
        public void SetInt(string n, int v)          => GL.Uniform1(Loc(n), v);
        public void SetFloat(string n, float v)      => GL.Uniform1(Loc(n), v);
        public void SetVec2(string n, Vector2 v)     => GL.Uniform2(Loc(n), v.X, v.Y);
        public void SetVec3(string n, Vector3 v)     => GL.Uniform3(Loc(n), v);
        public void SetMat4(string n, ref Matrix4 m) => GL.UniformMatrix4(Loc(n), false, ref m);
    }
 
// =============================================================================
//  SECTION 3 - SHARED MESH FACE TYPE
// =============================================================================
 
    public class MeshFace
    {
        public int[] VI;   // vertex indices (always length 3)
        public int[] TI;   // texcoord indices
        public int[] NI;   // normal indices  (-1 = none)
 
        public MeshFace(int[] vi, int[] ti, int[] ni)
        { VI = vi; TI = ti; NI = ni; }
    }
 
// =============================================================================
//  SECTION 4 - GPU MODEL CLASS
//  Receives parsed ModelData, uploads to GPU, handles textures & rendering.
// =============================================================================
 
    public class GPUModel
    {
        // Mesh data (kept for UV preview, stats, etc.)
        public List<Vector3>  Vertices  = new List<Vector3>();
        public List<Vector2>  TexCoords = new List<Vector2>();
        public List<Vector3>  Normals   = new List<Vector3>();
        public List<MeshFace> Faces     = new List<MeshFace>();
        public Vector3 BoundsMin, BoundsMax;
        public int EdgeCount { get; private set; }
 
        // Texture IDs (-1 = not loaded)
        public int ColorMapId = -1, NormalMapId = -1, SpecularMapId = -1;
        public int MetallicMapId = -1, RoughnessMapId = -1, OpacityMapId = -1;
 
        // Texture paths for all 6 slots (export + file-watching)
        public string[] TexPaths = new string[6];
        public string ColorMapPath => TexPaths[0];  // read-only alias
 
        // GPU handles
        public int VAO, VBO, EBO;
        private int _idxCount;
 
        private static readonly string[] TEX_NAMES =
            { "colorMap","normalMap","specularMap","roughnessMap","metallicMap","opacityMap" };
        private static readonly string[] HAS_NAMES =
            { "hasColorMap","hasNormalMap","hasSpecularMap","hasRoughnessMap","hasMetallicMap","hasOpacityMap" };
 
        // -- Extension dispatcher ----------------------------------------------
        public void LoadFromFile(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            List<Vector3>  v; List<Vector2> uv; List<Vector3> n;
            List<MeshFace> f; Vector3 bMin, bMax;
 
            try
            {
                switch (ext)
                {
                    case ".obj": (v, uv, n, f, bMin, bMax) = ObjLoader.Load(path); break;
                    case ".csv": (v, uv, n, f, bMin, bMax) = CsvLoader.Load(path); break;
                    case ".stl": (v, uv, n, f, bMin, bMax) = StlLoader.Load(path); break;
                    case ".rip":
                        // Ask user for field offsets via popup
                        int ripPos = 0, ripUv = -1;
                        if (RipFieldsSelector != null)
                        {
                            var ans = RipFieldsSelector(path);
                            if (!ans.HasValue) return; // user cancelled
                            ripPos = ans.Value.posOff;
                            ripUv  = ans.Value.uvOff;
                        }
                        (v, uv, n, f, bMin, bMax) = NR1Loader.Load(path, ripPos, ripUv);
                        break;
                    case ".nr":  (v, uv, n, f, bMin, bMax) = NR2Loader.Load(path); break;
                    case ".glb": (v, uv, n, f, bMin, bMax) = GlbLoader.Load(path); break;
                    case ".dae": (v, uv, n, f, bMin, bMax) = DaeLoader.Load(path); break;
                    case ".fbx": (v, uv, n, f, bMin, bMax) = FbxLoader.Load(path); break;
                    default:
                        MessageBox.Show($"Unsupported format: {ext}", "Load Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning); return;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading {Path.GetFileName(path)}:\n\n{ex.Message}",
                    "Load Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
 
            Vertices = v; TexCoords = uv; Normals = n; Faces = f;
            BoundsMin = bMin; BoundsMax = bMax;
 
            // Auto-generate normals when missing ? prevents flat/grey appearance
            if (Normals.Count == 0) RecalcNormals();
 
            BuildEdgeSet();
            if (ext == ".obj") LoadMtl(path);
            else               TryAutoTex(path);
            BuildBuffers();
        }
 
        public float   GetSize()   => Math.Max(Math.Max(BoundsMax.X - BoundsMin.X, BoundsMax.Y - BoundsMin.Y), BoundsMax.Z - BoundsMin.Z);
        public Vector3 GetCenter() => (BoundsMin + BoundsMax) * 0.5f;
        public bool    HasTex(int slot)
        {
            switch (slot)
            {
                case 0: return ColorMapId >= 0;    case 1: return NormalMapId >= 0;
                case 2: return SpecularMapId >= 0; case 3: return RoughnessMapId >= 0;
                case 4: return MetallicMapId >= 0; case 5: return OpacityMapId >= 0;
                default: return false;
            }
        }
 
        public int GetTexId(int slot)
        {
            switch (slot)
            {
                case 0: return ColorMapId;     case 1: return NormalMapId;
                case 2: return SpecularMapId;  case 3: return RoughnessMapId;
                case 4: return MetallicMapId;  case 5: return OpacityMapId;
                default: return -1;
            }
        }
 
        // Prompt shown before loading .rip files; set by Viewer3DForm on startup.
        // Returns (posByteOffset, uvByteOffset) or null to cancel.
        public static Func<string, (int posOff, int uvOff)?> RipFieldsSelector = null;
 
        // -- Normal recalculation (smooth) -------------------------------------
        public void RecalcNormals()
        {
            var acc = new Vector3[Vertices.Count];
            foreach (var face in Faces)
            {
                if (face.VI[0] >= Vertices.Count || face.VI[1] >= Vertices.Count || face.VI[2] >= Vertices.Count) continue;
                var e1 = Vertices[face.VI[1]] - Vertices[face.VI[0]];
                var e2 = Vertices[face.VI[2]] - Vertices[face.VI[0]];
                var fn = Vector3.Cross(e1, e2); fn.Normalize();
                acc[face.VI[0]] += fn; acc[face.VI[1]] += fn; acc[face.VI[2]] += fn;
            }
            Normals.Clear();
            for (int i = 0; i < acc.Length; i++) { acc[i].Normalize(); Normals.Add(acc[i]); }
            foreach (var face in Faces) for (int j = 0; j < 3; j++) face.NI[j] = face.VI[j];
        }
 
        // -- Edge counting -----------------------------------------------------
        private void BuildEdgeSet()
        {
            var set = new HashSet<long>();
            foreach (var f in Faces)
                for (int i = 0; i < 3; i++)
                {
                    int a = f.VI[i], b = f.VI[(i + 1) % 3];
                    set.Add(((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b));
                }
            EdgeCount = set.Count;
        }
 
        // -- GPU buffer upload -------------------------------------------------
        private void BuildBuffers()
        {
            if (VAO != 0) { GL.DeleteVertexArray(VAO); GL.DeleteBuffer(VBO); GL.DeleteBuffer(EBO); }
 
            var vd  = new List<float>();
            var idx = new List<uint>();
 
            for (int fi = 0; fi < Faces.Count; fi++)
            {
                var face = Faces[fi];
                for (int j = 0; j < 3; j++)
                {
                    int vi = Math.Min(face.VI[j], Vertices.Count - 1);
                    int ti = face.TI[j]; if (ti < 0 || ti >= TexCoords.Count) ti = -1;
                    int ni = face.NI[j]; if (ni < 0 || ni >= Normals.Count)   ni = -1;
 
                    var pos = Vertices[vi];
                    vd.Add(pos.X); vd.Add(pos.Y); vd.Add(pos.Z);
 
                    if (ni >= 0) { var nm = Normals[ni];   vd.Add(nm.X); vd.Add(nm.Y); vd.Add(nm.Z); }
                    else         {                          vd.Add(0f);   vd.Add(1f);   vd.Add(0f); }
 
                    if (ti >= 0) { var uv = TexCoords[ti]; vd.Add(uv.X); vd.Add(uv.Y); }
                    else         {                          vd.Add(0f);   vd.Add(0f); }
 
                    vd.Add(1f); vd.Add(0f); vd.Add(0f); // tangent placeholder
                    idx.Add((uint)(fi * 3 + j));
                }
            }
 
            ComputeTangents(vd, idx);
 
            VAO = GL.GenVertexArray(); VBO = GL.GenBuffer(); EBO = GL.GenBuffer();
            GL.BindVertexArray(VAO);
 
            GL.BindBuffer(BufferTarget.ArrayBuffer, VBO);
            GL.BufferData(BufferTarget.ArrayBuffer, vd.Count * sizeof(float), vd.ToArray(), BufferUsageHint.StaticDraw);
 
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, EBO);
            GL.BufferData(BufferTarget.ElementArrayBuffer, idx.Count * sizeof(uint), idx.ToArray(), BufferUsageHint.StaticDraw);
 
            int stride = 11 * sizeof(float);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);               GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float)); GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float)); GL.EnableVertexAttribArray(2);
            GL.VertexAttribPointer(3, 3, VertexAttribPointerType.Float, false, stride, 8 * sizeof(float)); GL.EnableVertexAttribArray(3);
            GL.BindVertexArray(0);
            _idxCount = idx.Count;
        }
 
        private static void ComputeTangents(List<float> vd, List<uint> idx)
        {
            for (int i = 0; i + 2 < idx.Count; i += 3)
            {
                int i0 = (int)idx[i] * 11, i1 = (int)idx[i + 1] * 11, i2 = (int)idx[i + 2] * 11;
                if (i2 + 10 >= vd.Count) continue;
 
                var v0 = new Vector3(vd[i0], vd[i0+1], vd[i0+2]);
                var v1 = new Vector3(vd[i1], vd[i1+1], vd[i1+2]);
                var v2 = new Vector3(vd[i2], vd[i2+1], vd[i2+2]);
                var d1 = new Vector2(vd[i1+6], vd[i1+7]) - new Vector2(vd[i0+6], vd[i0+7]);
                var d2 = new Vector2(vd[i2+6], vd[i2+7]) - new Vector2(vd[i0+6], vd[i0+7]);
                float denom = d1.X * d2.Y - d2.X * d1.Y;
                float ff = Math.Abs(denom) < 1e-6f ? 1f : 1f / denom;
                var e1 = v1 - v0; var e2 = v2 - v0;
                var T = new Vector3(ff*(d2.Y*e1.X - d1.Y*e2.X), ff*(d2.Y*e1.Y - d1.Y*e2.Y), ff*(d2.Y*e1.Z - d1.Y*e2.Z));
                T.Normalize();
                vd[i0+8]=T.X; vd[i0+9]=T.Y; vd[i0+10]=T.Z;
                vd[i1+8]=T.X; vd[i1+9]=T.Y; vd[i1+10]=T.Z;
                vd[i2+8]=T.X; vd[i2+9]=T.Y; vd[i2+10]=T.Z;
            }
        }
 
        // -- Texture loading ---------------------------------------------------
        private void LoadMtl(string objPath)
        {
            string mtl = Path.ChangeExtension(objPath, ".mtl");
            if (!File.Exists(mtl)) { TryAutoTex(objPath); return; }
            string dir = Path.GetDirectoryName(objPath);
            foreach (var raw in File.ReadAllLines(mtl))
            {
                var p = raw.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 2) continue;
                string texFile = p[p.Length - 1]; // last token = filename (handles -bm flag)
                string tp = Path.Combine(dir, texFile);
                if (!File.Exists(tp)) continue;
                if      (p[0] == "map_Kd")                         { ColorMapId    = LoadTex(tp); TexPaths[0] = tp; }
                else if (p[0] == "map_Bump" || p[0] == "bump")    { NormalMapId   = LoadTex(tp); TexPaths[1] = tp; }
                else if (p[0] == "map_Ks")                         { SpecularMapId = LoadTex(tp); TexPaths[2] = tp; }
                else if (p[0] == "map_Pm")                         { MetallicMapId = LoadTex(tp); TexPaths[4] = tp; }
                else if (p[0] == "map_Pr" || p[0] == "map_Ns")    { RoughnessMapId= LoadTex(tp); TexPaths[3] = tp; }
                else if (p[0] == "map_d")                          OpacityMapId  = LoadTex(tp);
            }
        }
 
        private void TryAutoTex(string modelPath)
        {
            string dir  = Path.GetDirectoryName(modelPath) ?? ".";
            string name = Path.GetFileNameWithoutExtension(modelPath);
            foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".bmp", ".dds", ".tga" })
            {
                string tp = Path.Combine(dir, name + ext);
                if (File.Exists(tp)) { ColorMapId = LoadTex(tp); TexPaths[0] = tp; return; }
            }
        }
 
        public void LoadTexture(string path, int slot)
        {
            int id = LoadTex(path); if (id < 0) return;
            switch (slot)
            {
                case 0: ColorMapId    = id; break;
                case 1: NormalMapId   = id; break;
                case 2: SpecularMapId = id; break;
                case 3: RoughnessMapId= id; break;
                case 4: MetallicMapId = id; break;
                case 5: OpacityMapId  = id; break;
            }
            if (slot >= 0 && slot < 6) TexPaths[slot] = path;
        }
 
        private int LoadTex(string path)
        {
            try
            {
                Bitmap bmp;
                string ext = Path.GetExtension(path).ToLower();
                if      (ext == ".dds") bmp = LoadDDS(path);
                else if (ext == ".tga") bmp = LoadTGA(path);
                else                    bmp = new Bitmap(path);
                if (bmp == null) return -1;
 
                bmp.RotateFlip(RotateFlipType.RotateNoneFlipY);
                var locked = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                    System.Drawing.Imaging.ImageLockMode.ReadOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
 
                int id = GL.GenTexture();
                GL.BindTexture(TextureTarget.Texture2D, id);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba,
                    bmp.Width, bmp.Height, 0, PixelFormat.Bgra, PixelType.UnsignedByte, locked.Scan0);
                GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
                bmp.UnlockBits(locked); bmp.Dispose();
                return id;
            }
            catch { return -1; }
        }
 
        public  Bitmap LoadDDSPublic(string path) => LoadDDS(path);
        public  Bitmap LoadTGAPublic(string path)  => LoadTGA(path);
        private Bitmap LoadDDS(string path)
        {
            try
            {
                using (var br = new BinaryReader(File.OpenRead(path)))
                {
                    // Full 128-byte DDS header
                    if (br.ReadUInt32() != 0x20534444u) return null; // magic "DDS "
                    br.ReadBytes(4);                     // dwSize
                    br.ReadBytes(4);                     // dwFlags
                    int height = br.ReadInt32();         // dwHeight (offset 12)
                    int width  = br.ReadInt32();         // dwWidth  (offset 16)
                    br.ReadBytes(4);                     // dwPitchOrLinearSize
                    br.ReadBytes(4);                     // dwDepth
                    br.ReadBytes(4);                     // dwMipMapCount
                    br.ReadBytes(44);                    // dwReserved1[11]
                    br.ReadBytes(4);                     // ddspf.dwSize
                    uint pfFlags = br.ReadUInt32();      // ddspf.dwFlags (offset 80)
                    uint fourCC  = br.ReadUInt32();      // ddspf.dwFourCC (offset 84)
                    br.ReadBytes(20);                    // rest of ddspf: RGBBitCount + 4 masks
                    br.ReadBytes(20);                    // dwCaps[4] + dwReserved2 => now at byte 128
                    // Now at offset 128 == first pixel byte
 
                    const uint DXT1 = 0x31545844u, DXT3 = 0x33545844u, DXT5 = 0x35545844u;
                    bool compressed = (pfFlags & 0x4u) != 0;
                    int w = width, h = height;
                    var pixels = new byte[w * h * 4]; // BGRA output
 
                    if (compressed && (fourCC == DXT1 || fourCC == DXT3 || fourCC == DXT5))
                    {
                        int blkBytes = (fourCC == DXT1) ? 8 : 16;
                        int bw = Math.Max(1, (w+3)/4), bh = Math.Max(1, (h+3)/4);
 
                        for (int by = 0; by < bh; by++)
                        for (int bx = 0; bx < bw; bx++)
                        {
                            byte[] blk = br.ReadBytes(blkBytes);
                            int ao = (fourCC == DXT1) ? 0 : 8; // colour-block offset
 
                            // DXT5 alpha block
                            long aBits = 0; byte a0 = 255, a1 = 255;
                            if (fourCC == DXT5)
                            {
                                a0 = blk[0]; a1 = blk[1];
                                aBits = (long)blk[2]        | ((long)blk[3] << 8)  |
                                        ((long)blk[4] << 16)| ((long)blk[5] << 24) |
                                        ((long)blk[6] << 32)| ((long)blk[7] << 40);
                            }
 
                            // Decode two 16-bit RGB565 endpoint colours
                            ushort c0 = (ushort)(blk[ao]   | (blk[ao+1]<<8));
                            ushort c1 = (ushort)(blk[ao+2] | (blk[ao+3]<<8));
                            uint   ci = (uint)  (blk[ao+4] | (blk[ao+5]<<8) | (blk[ao+6]<<16) | (blk[ao+7]<<24));
 
                            int r0=(c0>>11&31)*255/31, g0=(c0>>5&63)*255/63, b0=(c0&31)*255/31;
                            int r1=(c1>>11&31)*255/31, g1=(c1>>5&63)*255/63, b1=(c1&31)*255/31;
 
                            int[] cr=new int[4], cg=new int[4], cb=new int[4], ca=new int[]{255,255,255,255};
                            cr[0]=r0; cg[0]=g0; cb[0]=b0;
                            cr[1]=r1; cg[1]=g1; cb[1]=b1;
                            if (fourCC != DXT1 || c0 > c1)
                            {
                                cr[2]=(2*r0+r1)/3; cg[2]=(2*g0+g1)/3; cb[2]=(2*b0+b1)/3;
                                cr[3]=(r0+2*r1)/3; cg[3]=(g0+2*g1)/3; cb[3]=(b0+2*b1)/3;
                                if (fourCC == DXT1) ca[3] = 0;
                            }
                            else
                            {
                                cr[2]=(r0+r1)/2; cg[2]=(g0+g1)/2; cb[2]=(b0+b1)/2;
                                cr[3]=0; cg[3]=0; cb[3]=0; ca[3]=0;
                            }
 
                            for (int py = 0; py < 4; py++)
                            for (int px = 0; px < 4; px++)
                            {
                                int px2=bx*4+px, py2=by*4+py;
                                if (px2>=w||py2>=h) continue;
                                int pi=(py2*w+px2)*4;
                                int ii=(int)((ci>>(2*(py*4+px)))&3);
                                pixels[pi  ]=(byte)cb[ii]; // B
                                pixels[pi+1]=(byte)cg[ii]; // G
                                pixels[pi+2]=(byte)cr[ii]; // R
 
                                if (fourCC == DXT5)
                                {
                                    int ai = py*4+px;
                                    int alphaIdx=(int)((aBits>>(ai*3))&7);
                                    int alpha;
                                    if      (alphaIdx==0) alpha=a0;
                                    else if (alphaIdx==1) alpha=a1;
                                    else if (a0>a1)       alpha=((8-alphaIdx)*a0+(alphaIdx-1)*a1)/7;
                                    else if (alphaIdx<=5) alpha=((6-alphaIdx)*a0+(alphaIdx-1)*a1)/5;
                                    else                  alpha=alphaIdx==6?0:255;
                                    pixels[pi+3]=(byte)alpha;
                                }
                                else pixels[pi+3]=(byte)ca[ii];
                            }
                        }
                    }
                    else if ((pfFlags & 0x40u) != 0) // uncompressed RGB(A)
                    {
                        bool hasAlpha = (pfFlags & 0x1u) != 0;
                        for (int i=0; i<w*h*4; i+=4)
                        {
                            pixels[i+2]=br.ReadByte(); // R
                            pixels[i+1]=br.ReadByte(); // G
                            pixels[i  ]=br.ReadByte(); // B
                            pixels[i+3]=hasAlpha ? br.ReadByte() : (byte)255;
                        }
                    }
                    else return null;
 
                    var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    var bd  = bmp.LockBits(new Rectangle(0,0,w,h),
                        System.Drawing.Imaging.ImageLockMode.WriteOnly,
                        System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bd.Scan0, pixels.Length);
                    bmp.UnlockBits(bd);
                    return bmp;
                }
            }
            catch { return null; }
        }
 
        private Bitmap LoadTGA(string path)
        {
            try
            {
                using (var br = new BinaryReader(File.OpenRead(path)))
                {
                    br.ReadBytes(2); byte type = br.ReadByte(); br.ReadBytes(9);
                    short w = br.ReadInt16(), h = br.ReadInt16();
                    byte bpp = br.ReadByte(); br.ReadByte();
                    int bpx = bpp / 8;
                    var px  = br.ReadBytes(w * h * bpx);
                    var bmp = new Bitmap(w, h);
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                        {
                            int i = (y * w + x) * bpx;
                            byte a = bpx == 4 ? px[i+3] : (byte)255;
                            bmp.SetPixel(x, y, Color.FromArgb(a, px[i+2], px[i+1], px[i]));
                        }
                    return bmp;
                }
            }
            catch { return null; }
        }
 
        // -- GPU Render --------------------------------------------------------
        public void Render(Shader sh, int mode)
        {
            sh.Use();
            sh.SetInt("shadingMode", mode);
            sh.SetVec3("solidColor", new Vector3(0.86f, 0.86f, 0.86f));
 
            bool doTex = (mode == 2);
            BindTex(sh, 0, ColorMapId,     doTex);
            BindTex(sh, 1, NormalMapId,    doTex);
            BindTex(sh, 2, SpecularMapId,  doTex);
            BindTex(sh, 3, RoughnessMapId, doTex);
            BindTex(sh, 4, MetallicMapId,  doTex);
            BindTex(sh, 5, OpacityMapId,   doTex);
 
            GL.BindVertexArray(VAO);
            GL.DrawElements(PrimitiveType.Triangles, _idxCount, DrawElementsType.UnsignedInt, 0);
            GL.BindVertexArray(0);
        }
 
        private static void BindTex(Shader sh, int unit, int id, bool enable)
        {
            GL.ActiveTexture(TextureUnit.Texture0 + unit);
            GL.BindTexture(TextureTarget.Texture2D, enable && id >= 0 ? id : 0);
            sh.SetInt(TEX_NAMES[unit], unit);
            sh.SetInt(HAS_NAMES[unit], enable && id >= 0 ? 1 : 0);
        }
 
        public void Cleanup()
        {
            if (VAO != 0) { GL.DeleteVertexArray(VAO); GL.DeleteBuffer(VBO); GL.DeleteBuffer(EBO); VAO = VBO = EBO = 0; }
        }
    }
 
// =============================================================================
//  SECTION 5 - FORM SETUP & UI CONTROLS
// =============================================================================
 
    public class Viewer3DForm : Form
    {
        // -- GL & panels --
        private GLControl glControl;
        private TabControl tabControl;
        private TabPage envLightTab, statsShadingTab;
        private Panel lightPanel, previewPanel;
 
        // -- Labels & buttons --
        private Label vertLabel, triLabel, edgeLabel, loadedLabel, noTexLabel;
        private Button loadBtn;
        private Button solidBtn, wireBtn, texBtn;
        private Button colBtn, nrmBtn, specBtn, roughBtn, metBtn, opqBtn;
        private Button gridBtn, axesBtn, showTexBtn, uvBtn;
 
        // -- Theme colours --
        private readonly Color BG      = Color.FromArgb(240, 240, 240);
        private readonly Color IDLE    = Color.FromArgb(225, 225, 225);
        private readonly Color HOVER   = Color.FromArgb(229, 241, 251);
        private readonly Color PRESS   = Color.FromArgb(204, 228, 247);
        private readonly Color BORDER  = Color.FromArgb(173, 173, 173);
        private readonly Color BORDERA = Color.FromArgb(0, 120, 215);
 
        // -- Camera --
        private float rotX = 0f, rotY = 0f;
        private float zoom = -5f;
        private Vector3 lookAt = Vector3.Zero;
        private const float ROT_MIN = -89f, ROT_MAX = 89f;
        private const float ZOOM_NEAR = -0.2f, ZOOM_FAR = -5000f;
 
        // -- Input --
        private bool dragRot, dragPan, dragZoomMid;
        private Point lastMouse;
        private DateTime lastClick = DateTime.MinValue;
 
        // -- Light --
        private float lightAngle = 0.5f;
        private bool  dragLight;
 
        // -- Scene --
        private GPUModel model;
        private bool showCube  = true;
        private int  shadeMode = 0;   // 0=solid 1=wire 2=tex
        private int  texSlot   = 0;
        private bool showUV, showGrid = true, showAxes, showTexPreview;
        private bool darkTheme = false;   // false = light (default), true = dark
        private Vector3 cachedCenter = Vector3.Zero;
        private float   cachedSize   = 5f;
 
        // -- GL resources --
        private Shader mainShader, wireShader;
        private int cubeVAO, cubeVBO;
 
        // -- UV / texture preview cache --
        private Bitmap uvCache;
        private Bitmap texPreviewBmp;
        private bool   uvDirty = true;
        // -- Texture file watchers (auto-reload on external edit) --
        private readonly System.IO.FileSystemWatcher[] _texWatchers = new System.IO.FileSystemWatcher[6];
        private readonly DateTime[] _lastReload = new DateTime[6];
        // -- Smooth theme transition (0=light, 1=dark) --
        private float _themeT      = 0f;
        private float _themeTarget = 0f;
        private System.Windows.Forms.Timer _themeTimer;
 
// =============================================================================
//  SECTION 6 - FORM CONSTRUCTION & UI LAYOUT
// =============================================================================
 
        public Viewer3DForm()
        {
            BuildUI();
 
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                using (var s = asm.GetManifestResourceStream("Viewer3D.1.ico"))
                    if (s != null) Icon = new Icon(s);
            }
            catch { }
 
            Load      += (s, e) => InitGL();
            KeyPreview = true;
            KeyDown   += OnKey;
            glControl.Resize += (s, e) => { GL.Viewport(0, 0, glControl.Width, glControl.Height); glControl.Invalidate(); };
            UpdateStatLabels();
        }
 
        private void BuildUI()
        {
            Text        = "HotDog ? 3D Viewer";
            ClientSize  = new Size(1280, 960);
            MinimumSize = new Size(800, 600);
            AllowDrop   = true;
            DragEnter  += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop   += OnDrop;
 
            // -- OpenGL control (fills left area) --
            glControl = new GLControl(new GraphicsMode(32, 24, 0, 4), 3, 3, GraphicsContextFlags.ForwardCompatible)
                { Dock = DockStyle.Fill };
            glControl.Paint      += OnPaint;
            glControl.MouseDown  += OnMouseDown;
            glControl.MouseUp    += OnMouseUp;
            glControl.MouseMove  += OnMouseMove;
            glControl.MouseWheel += OnWheel;
            Controls.Add(glControl);
 
            // -- "Load Model" overlay button --
            loadBtn = Btn(null, "Load Model", 10, 10, 120, 36);
            loadBtn.Click += (s, e) => OpenDialog();
            glControl.Controls.Add(loadBtn);
 
            noTexLabel = new Label
            {
                Text = "No texture found",
                Location = new Point(10, 54), Size = new Size(200, 22),
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                ForeColor = Color.FromArgb(210, 55, 55), BackColor = Color.Transparent, Visible = false
            };
            glControl.Controls.Add(noTexLabel);
 
            // -- Right panel --
            tabControl = new TabControl { Dock = DockStyle.Right, Width = 290, Appearance = TabAppearance.FlatButtons };
            Controls.Add(tabControl);
 
            // --- Env & Light tab ---
            envLightTab = new TabPage("Env & Light") { BackColor = BG };
            tabControl.TabPages.Add(envLightTab);
 
            envLightTab.Controls.Add(new Label { Text = "Light Direction", Font = new Font("Segoe UI", 9, FontStyle.Bold), Location = new Point(70, 10), AutoSize = true, BackColor = BG });
 
            lightPanel = new Panel
            {
                Location = new Point(55, 35), Size = new Size(180, 180),
                BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle
            };
            lightPanel.Paint     += OnLightPaint;
            lightPanel.MouseDown += (s, e) => { dragLight = true;  UpdateLight(e.Location); };
            lightPanel.MouseUp   += (s, e) =>   dragLight = false;
            lightPanel.MouseMove += (s, e) => { if (dragLight) UpdateLight(e.Location); };
            envLightTab.Controls.Add(lightPanel);
 
            envLightTab.Controls.Add(new Label { Text = "Drag the dot to orbit the\nkey light around the model.",
                Location = new Point(55, 225), Size = new Size(180, 36), Font = new Font("Segoe UI", 8),
                ForeColor = Color.FromArgb(100, 100, 100), BackColor = BG });
 
            // --- Stats & Shading tab ---
            statsShadingTab = new TabPage("Stats & Shading") { BackColor = BG };
            tabControl.TabPages.Add(statsShadingTab);
 
            int y = 10;
            loadedLabel = new Label { Text = "Default Cube", Font = new Font("Segoe UI", 10, FontStyle.Bold),
                Location = new Point(10, y), Size = new Size(270, 26), BackColor = BG };
            statsShadingTab.Controls.Add(loadedLabel); y += 34;
 
            // UV preview panel (toggled by button; always pre-rendered)
            previewPanel = new Panel
            {
                Location = new Point(10, y), Size = new Size(270, 270),
                BackColor = Color.FromArgb(30,  30,  30),  BorderStyle = BorderStyle.FixedSingle
            };
            previewPanel.Paint  += OnUVPaint;
            previewPanel.Resize += (s, e) => { uvDirty = true; previewPanel.Invalidate(); };
            statsShadingTab.Controls.Add(previewPanel); y += 280;
 
            // Shading
            solidBtn = SBtn(statsShadingTab, "Solid Shading", ref y); solidBtn.BackColor = PRESS;
            solidBtn.Click += (s, e) => { shadeMode = 0; RefreshShade(); };
 
            wireBtn = SBtn(statsShadingTab, "Wireframe View", ref y);
            wireBtn.Click  += (s, e) => { shadeMode = 1; RefreshShade(); };
 
            texBtn = SBtn(statsShadingTab, "Texture View", ref y);
            texBtn.Click   += (s, e) => { shadeMode = 2; RefreshShade(); };
            y += 8;
 
            // Texture maps group
            var grp = new GroupBox { Text = "Texture Maps", Location = new Point(10, y), Size = new Size(270, 186), BackColor = BG };
            statsShadingTab.Controls.Add(grp);
            int gy = 22;
            colBtn  = TBtn(grp, "Color",    ref gy); colBtn.BackColor = PRESS;
            nrmBtn  = TBtn(grp, "Normal",   ref gy);
            specBtn = TBtn(grp, "Specular", ref gy);
            roughBtn= TBtn(grp, "Roughness",ref gy);
            metBtn  = TBtn(grp, "Metallic", ref gy);
            opqBtn  = TBtn(grp, "Opacity",  ref gy);
            colBtn.Click  += (s, e) => SetSlot(0); nrmBtn.Click  += (s, e) => SetSlot(1);
            specBtn.Click += (s, e) => SetSlot(2); roughBtn.Click+= (s, e) => SetSlot(3);
            metBtn.Click  += (s, e) => SetSlot(4); opqBtn.Click  += (s, e) => SetSlot(5);
            y += 196;
 
            gridBtn = SBtn(statsShadingTab, "Show Grid", ref y);
            gridBtn.BackColor = PRESS;  // on by default
            gridBtn.Click += (s, e) => { showGrid = !showGrid; gridBtn.BackColor = showGrid ? PRESS : IDLE; glControl.Invalidate(); };
 
            axesBtn = SBtn(statsShadingTab, "Show Axes", ref y);
            axesBtn.Click += (s, e) => { showAxes = !showAxes; axesBtn.BackColor = showAxes ? PRESS : IDLE; glControl.Invalidate(); };
 
            showTexBtn = SBtn(statsShadingTab, "Show Texture", ref y);
            showTexBtn.Click += (s, e) =>
            {
                showTexPreview = !showTexPreview;
                showTexBtn.BackColor = showTexPreview ? PRESS : IDLE;
                previewPanel.Invalidate();
            };
 
            uvBtn = SBtn(statsShadingTab, "Show UV Preview", ref y);
            uvBtn.Click += (s, e) =>
            {
                showUV = !showUV;
                uvBtn.BackColor = showUV ? PRESS : IDLE;
                if (showUV) { uvDirty = true; }
                previewPanel.Invalidate();
            };
            y += 12;
 
            statsShadingTab.Controls.Add(new Label { Text = "Model Statistics", Font = new Font("Segoe UI", 10, FontStyle.Bold),
                Location = new Point(10, y), AutoSize = true, BackColor = BG }); y += 28;
 
            vertLabel = Lbl("Vertices: 8",   10, ref y); statsShadingTab.Controls.Add(vertLabel);
            triLabel  = Lbl("Triangles: 12", 10, ref y); statsShadingTab.Controls.Add(triLabel);
            edgeLabel = Lbl("Edges: 18",     10, ref y); statsShadingTab.Controls.Add(edgeLabel);
            y += 12;
 
            statsShadingTab.Controls.Add(new Label { Text = "Shortcuts:  R = reset  |  F = focus model  |  M = theme  |  N = normals",
                Location = new Point(10, y), Size = new Size(270, 32), Font = new Font("Segoe UI", 8),
                ForeColor = Color.FromArgb(110, 110, 110), BackColor = BG });
 
            RefreshTexBtnEnabled();
        }
 
        // -- UI helper: styled button --
        private Button Btn(Control parent, string txt, int x, int y, int w, int h)
        {
            var b = new Button { Text = txt, Location = new Point(x, y), Size = new Size(w, h), FlatStyle = FlatStyle.Flat, BackColor = IDLE };
            b.FlatAppearance.BorderColor = BORDER;
            b.MouseEnter += (s, e) => { if (b.Enabled) { b.BackColor = HOVER;  b.FlatAppearance.BorderColor = BORDERA; } };
            b.MouseLeave += (s, e) => { if (b.Enabled) { b.BackColor = b.BackColor == PRESS ? PRESS : IDLE;  b.FlatAppearance.BorderColor = BORDER; } };
            b.MouseDown  += (s, e) => { if (b.Enabled)   b.BackColor = PRESS; };
            b.MouseUp    += (s, e) => { if (b.Enabled)   b.BackColor = HOVER; };
            parent?.Controls.Add(b);
            return b;
        }
 
        private Button SBtn(Control p, string txt, ref int y)
        {
            var b = Btn(p, txt, 10, y, 270, 28); y += 32; return b;
        }
 
        private Button TBtn(Control p, string txt, ref int y)
        {
            var b = Btn(p, txt, 8, y, 254, 22); y += 26; return b;
        }
 
        private Label Lbl(string txt, int x, ref int y)
        {
            var l = new Label { Text = txt, Location = new Point(x, y), AutoSize = true, BackColor = BG }; y += 22; return l;
        }
 
// =============================================================================
//  SECTION 7 - GL SETUP & SCENE RENDERING
// =============================================================================
 
        private void InitGL()
        {
            glControl.MakeCurrent();
            mainShader = new Shader(ShaderSource.Vert,    ShaderSource.Frag);
            wireShader = new Shader(ShaderSource.WireVert, ShaderSource.WireFrag);
 
            GL.Enable(EnableCap.DepthTest);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.Enable(EnableCap.LineSmooth);
            GL.Hint(HintTarget.LineSmoothHint, HintMode.Nicest);
            // Light theme by default (M key toggles dark/light)
            GL.ClearColor(0.867f,0.867f,0.867f,1f);
 
            BuildCubeGPU();
            GL.Viewport(0, 0, glControl.Width, glControl.Height);
 
            // Smooth theme transition timer (~60fps)
            _themeTimer = new System.Windows.Forms.Timer { Interval = 16 };
            _themeTimer.Tick += (s, e) =>
            {
                float step = 0.06f;
                float diff = _themeTarget - _themeT;
                if (Math.Abs(diff) <= step) { _themeT = _themeTarget; _themeTimer.Stop(); }
                else                          _themeT += diff > 0 ? step : -step;
                // Rerender UV box background during transition
                previewPanel.Invalidate();
                glControl.Invalidate();
            };
        }
 
        private void BuildCubeGPU()
        {
            float[] cv =
            {
                -1,-1, 1,  0, 0, 1,  0,0,  1,0,0,   1,-1, 1,  0, 0, 1,  1,0,  1,0,0,
                 1, 1, 1,  0, 0, 1,  1,1,  1,0,0,  -1, 1, 1,  0, 0, 1,  0,1,  1,0,0,
                -1,-1,-1,  0, 0,-1,  1,0,  1,0,0,  -1, 1,-1,  0, 0,-1,  1,1,  1,0,0,
                 1, 1,-1,  0, 0,-1,  0,1,  1,0,0,   1,-1,-1,  0, 0,-1,  0,0,  1,0,0,
                -1,-1,-1, -1, 0, 0,  0,0,  0,1,0,  -1,-1, 1, -1, 0, 0,  1,0,  0,1,0,
                -1, 1, 1, -1, 0, 0,  1,1,  0,1,0,  -1, 1,-1, -1, 0, 0,  0,1,  0,1,0,
                 1,-1,-1,  1, 0, 0,  0,0,  0,1,0,   1, 1,-1,  1, 0, 0,  0,1,  0,1,0,
                 1, 1, 1,  1, 0, 0,  1,1,  0,1,0,   1,-1, 1,  1, 0, 0,  1,0,  0,1,0,
                -1, 1,-1,  0, 1, 0,  0,0,  0,0,1,  -1, 1, 1,  0, 1, 0,  0,1,  0,0,1,
                 1, 1, 1,  0, 1, 0,  1,1,  0,0,1,   1, 1,-1,  0, 1, 0,  1,0,  0,0,1,
                -1,-1,-1,  0,-1, 0,  0,0,  0,0,1,   1,-1,-1,  0,-1, 0,  1,0,  0,0,1,
                 1,-1, 1,  0,-1, 0,  1,1,  0,0,1,  -1,-1, 1,  0,-1, 0,  0,1,  0,0,1
            };
            uint[] ci = { 0,1,2,2,3,0, 4,5,6,6,7,4, 8,9,10,10,11,8, 12,13,14,14,15,12, 16,17,18,18,19,16, 20,21,22,22,23,20 };
 
            cubeVAO = GL.GenVertexArray(); cubeVBO = GL.GenBuffer();
            int cebo = GL.GenBuffer();
            GL.BindVertexArray(cubeVAO);
            GL.BindBuffer(BufferTarget.ArrayBuffer, cubeVBO);
            GL.BufferData(BufferTarget.ArrayBuffer, cv.Length * sizeof(float), cv, BufferUsageHint.StaticDraw);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, cebo);
            GL.BufferData(BufferTarget.ElementArrayBuffer, ci.Length * sizeof(uint), ci, BufferUsageHint.StaticDraw);
            int stride = 11 * sizeof(float);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);               GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3*sizeof(float)); GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, 6*sizeof(float)); GL.EnableVertexAttribArray(2);
            GL.VertexAttribPointer(3, 3, VertexAttribPointerType.Float, false, stride, 8*sizeof(float)); GL.EnableVertexAttribArray(3);
            GL.BindVertexArray(0);
        }
 
        private void OnPaint(object sender, PaintEventArgs e)
        {
            glControl.MakeCurrent();
            // Smooth theme transition: interpolate between light (#DDDDDD) and dark (0.18)
            float bg = 0.867f * (1f - _themeT) + 0.18f * _themeT;
            GL.ClearColor(bg, bg, bg, 1f);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
 
            float aspect = (float)glControl.Width / Math.Max(glControl.Height, 1);
            Matrix4 proj = Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(45f), aspect, 0.01f, 5000f);
 
            // -- Arcball camera (orbits around lookAt; model stays at its OBJ coords) --
            float dist = Math.Abs(zoom);
            float rX = MathHelper.DegreesToRadians(rotX);
            float rY = MathHelper.DegreesToRadians(rotY);
            Vector3 offset = new Vector3(
                dist * (float)(Math.Sin(rY) * Math.Cos(rX)),
                dist * (float)Math.Sin(rX),
                dist * (float)(Math.Cos(rY) * Math.Cos(rX)));
            Vector3 camPos = lookAt + offset;
            Matrix4 view   = Matrix4.LookAt(camPos, lookAt, Vector3.UnitY);
 
            // Lights at fixed distance relative to model size (zoom-independent)
            float ld  = Math.Max(cachedSize * 3f, 10f);
            Vector3 lp1 = new Vector3(ld*(float)Math.Cos(lightAngle), ld*0.6f, ld*(float)Math.Sin(lightAngle));
            Vector3 lp2 = new Vector3(ld*(float)Math.Cos(lightAngle+(float)Math.PI), ld*0.4f, ld*(float)Math.Sin(lightAngle+(float)Math.PI));
 
            // Model stays at its original coordinate space (no centering translation)
            Matrix4 modelMat = Matrix4.Identity;
 
            // -- Wireframe mode --
            if (shadeMode == 1)
            {
                wireShader.Use();
                wireShader.SetMat4("projection", ref proj);
                wireShader.SetMat4("view",       ref view);
                wireShader.SetMat4("model",      ref modelMat);
 
                // Solid fill pass (coloured)
                wireShader.SetVec3("color", new Vector3(0.86f, 0.86f, 0.86f));
                DrawMesh();
 
                // Line pass (black edges) – slightly thicker to survive MSAA
                GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Line);
                GL.LineWidth(1.6f);
                wireShader.SetVec3("color", darkTheme ? new Vector3(0.08f, 0.08f, 0.08f) : new Vector3(0.05f, 0.05f, 0.05f));
                DrawMesh();
                GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
            }
            else
            {
                // -- Solid / Texture mode --
                mainShader.Use();
                mainShader.SetVec3("lightPos",  lp1);
                mainShader.SetVec3("lightPos2", lp2);
                mainShader.SetVec3("viewPos",   camPos);
                mainShader.SetMat4("projection", ref proj);
                mainShader.SetMat4("view",       ref view);
                mainShader.SetMat4("model",      ref modelMat);
 
                if (showCube)
                {
                    mainShader.SetInt("shadingMode", 0);
                    mainShader.SetVec3("solidColor", new Vector3(0.5f, 0.7f, 1f));
                    for (int i = 0; i < 6; i++) { GL.ActiveTexture(TextureUnit.Texture0 + i); GL.BindTexture(TextureTarget.Texture2D, 0); }
                    mainShader.SetInt("hasColorMap",0); mainShader.SetInt("hasNormalMap",0); mainShader.SetInt("hasSpecularMap",0);
                    mainShader.SetInt("hasRoughnessMap",0); mainShader.SetInt("hasMetallicMap",0); mainShader.SetInt("hasOpacityMap",0);
                    GL.BindVertexArray(cubeVAO);
                    GL.DrawElements(PrimitiveType.Triangles, 36, DrawElementsType.UnsignedInt, 0);
                    GL.BindVertexArray(0);
                }
                else model?.Render(mainShader, shadeMode);
            }
 
            // -- Grid & Axes ? always at world origin (0,0,0) --
            if (showGrid || showAxes)
            {
                wireShader.Use();
                wireShader.SetMat4("projection", ref proj);
                wireShader.SetMat4("view",       ref view);
                wireShader.SetMat4("model",      ref modelMat);
                GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
 
                var lv = new List<float>();
 
                if (showGrid)
                {
                    float gs   = Math.Max(cachedSize, 2f) * 2f;
                    float step = gs / 10f;
                    float yFloor = !showCube && model != null ? model.BoundsMin.Y : 0f;
                    for (int gi = -10; gi <= 10; gi++)
                    {
                        lv.AddRange(new[] { gi*step, yFloor, -gs,   gi*step, yFloor,  gs });  // Z lines
                        lv.AddRange(new[] { -gs, yFloor, gi*step,    gs, yFloor, gi*step });  // X lines
                    }
                }
 
                if (showAxes)
                {
                    // Short axes at world origin
                    float al = Math.Max(cachedSize * 0.12f, 0.5f);
                    lv.AddRange(new float[] { 0,0,0,  al,0,0  });  // X ? red
                    lv.AddRange(new float[] { 0,0,0,  0,al,0  });  // Y ? green
                    lv.AddRange(new float[] { 0,0,0,  0,0,al  });  // Z ? blue
                }
 
                float[] lva  = lv.ToArray();
                int tVAO = GL.GenVertexArray(), tVBO = GL.GenBuffer();
                GL.BindVertexArray(tVAO);
                GL.BindBuffer(BufferTarget.ArrayBuffer, tVBO);
                GL.BufferData(BufferTarget.ArrayBuffer, lva.Length * sizeof(float), lva, BufferUsageHint.StreamDraw);
                GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3*sizeof(float), 0);
                GL.EnableVertexAttribArray(0);
 
                if (showGrid)
                {
                    GL.LineWidth(1.8f);
                    var gc = darkTheme ? new Vector3(0.42f, 0.42f, 0.42f) : new Vector3(0.15f, 0.15f, 0.15f);
                    wireShader.SetVec3("color", gc);
                    GL.DrawArrays(PrimitiveType.Lines, 0, 21 * 4); // 21 rows ? 2 lines ? 2 verts
                }
                if (showAxes)
                {
                    GL.LineWidth(2.5f);
                    int axOff = showGrid ? 21 * 4 : 0;
                    wireShader.SetVec3("color", new Vector3(0.9f, 0.15f, 0.15f)); GL.DrawArrays(PrimitiveType.Lines, axOff,     2);
                    wireShader.SetVec3("color", new Vector3(0.15f, 0.85f, 0.15f));GL.DrawArrays(PrimitiveType.Lines, axOff + 2, 2);
                    wireShader.SetVec3("color", new Vector3(0.15f, 0.4f, 0.95f)); GL.DrawArrays(PrimitiveType.Lines, axOff + 4, 2);
                    GL.LineWidth(1f);
                }
 
                GL.BindVertexArray(0);
                GL.DeleteVertexArray(tVAO); GL.DeleteBuffer(tVBO);
            }
 
            glControl.SwapBuffers();
        }
 
        // Draws whichever mesh is active (cube or loaded model)
        private void DrawMesh()
        {
            if (showCube)
            {
                GL.BindVertexArray(cubeVAO);
                GL.DrawElements(PrimitiveType.Triangles, 36, DrawElementsType.UnsignedInt, 0);
            }
            else if (model != null)
            {
                GL.BindVertexArray(model.VAO);
                GL.DrawElements(PrimitiveType.Triangles, model.Faces.Count * 3, DrawElementsType.UnsignedInt, 0);
            }
            GL.BindVertexArray(0);
        }
 
// =============================================================================
//  SECTION 8 - UV PREVIEW  (pre-rendered to bitmap; only rebuilds on model change)
// =============================================================================
 
        private void OnUVPaint(object sender, PaintEventArgs e)
        {
            // Always dark background regardless of app theme
            e.Graphics.Clear(Color.FromArgb(30, 30, 30));
 
            if (!showUV || model == null || model.TexCoords.Count == 0)
            {
                using (var f = new Font("Segoe UI", 9))
                    e.Graphics.DrawString("No UV data", f, Brushes.Gray, 10, 10);
                return;
            }
 
            // Rebuild the cached bitmap only once (when model changes or panel resizes)
            if (uvDirty || uvCache == null)
            {
                RebuildUVCache();
                uvDirty = false;
            }
 
            if (uvCache != null)
                e.Graphics.DrawImage(uvCache, 0, 0);
        }
 
        private void RebuildUVCache()
        {
            uvCache?.Dispose(); uvCache = null;
            int pw = previewPanel.Width, ph = previewPanel.Height;
            if (pw <= 2 || ph <= 2 || model == null) return;
 
            var bmp = new Bitmap(pw, ph, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                // Transparent so texture shows through when both are active
                g.Clear(Color.Transparent);
 
                const float pad = 10f;
                float scale = Math.Min(pw - pad * 2, ph - pad * 2);
 
                // UV box always uses the dark-mode palette regardless of theme
                var gridColor = Color.FromArgb(70, 100, 100, 100);
                using (var gp = new Pen(gridColor, 0.5f))
                    for (int i = 0; i <= 4; i++)
                    {
                        float p = pad + (i / 4f) * scale;
                        g.DrawLine(gp, p, pad, p, pad + scale);
                        g.DrawLine(gp, pad, p, pad + scale, p);
                    }
 
                // UV-space boundary
                var borderColor = Color.FromArgb(95, 95, 95);
                g.DrawRectangle(new Pen(borderColor, 1f), pad, pad, scale, scale);
 
                // UV edges – always classic orange on dark
                var lineColor = Color.FromArgb(255, 165, 0);
                using (var pen = new Pen(lineColor, 1f))
                {
                    var drawn = new HashSet<long>();
                    foreach (var face in model.Faces)
                        for (int i = 0; i < face.TI.Length; i++)
                        {
                            int a = face.TI[i], b = face.TI[(i + 1) % face.TI.Length];
                            if (a < 0 || a >= model.TexCoords.Count) continue;
                            if (b < 0 || b >= model.TexCoords.Count) continue;
                            long key = ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
                            if (!drawn.Add(key)) continue;
                            var uv1 = model.TexCoords[a];
                            var uv2 = model.TexCoords[b];
                            g.DrawLine(pen,
                                pad + uv1.X * scale, pad + (1f - uv1.Y) * scale,
                                pad + uv2.X * scale, pad + (1f - uv2.Y) * scale);
                        }
                }
            }
            uvCache = bmp;
        }
 
// =============================================================================
//  SECTION 9 - CAMERA & INPUT
// =============================================================================
 
        private void OnKey(object sender, KeyEventArgs e)
        {
            if      (e.KeyCode == Keys.R) { ResetCam();    e.Handled = e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Return)
            {
                // SuppressKeyPress stops Enter from activating the focused button
                e.Handled = e.SuppressKeyPress = true;
                ExportOBJ();
            }
            else if (e.KeyCode == Keys.F) { FocusModel();  e.Handled = e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.M) { ToggleTheme(); e.Handled = e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.N && model != null)
            {
                model.RecalcNormals();
                glControl.Invalidate();
                e.Handled = e.SuppressKeyPress = true;
            }
        }
 
        private void FocusModel()
        {
            if (model == null) return;
            cachedCenter = model.GetCenter();
            cachedSize   = model.GetSize();
            lookAt       = cachedCenter;
            zoom         = -cachedSize * 1.8f;
            rotX = 0f; rotY = 0f;
            glControl.Invalidate();
        }
 
        private void ToggleTheme()
        {
            darkTheme      = !darkTheme;
            _themeTarget   = darkTheme ? 1f : 0f;
            _themeTimer.Start();   // animates _themeT toward _themeTarget
            uvDirty = true;
            previewPanel.Invalidate();
        }
 
        // -------------------------------------------------------------------
        // OBJ Export  (Enter key)  - inside Viewer3DForm
        // -------------------------------------------------------------------
        private void ExportOBJ()
        {
            if (model == null)
            {
                MessageBox.Show("No model loaded.", "Export OBJ",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (var dlg = new SaveFileDialog())
            {
                dlg.Title    = "Export OBJ";
                dlg.Filter   = "Wavefront OBJ (*.obj)|*.obj";
                dlg.FileName = "model.obj";
                if (dlg.ShowDialog() != DialogResult.OK) return;
 
                string objPath  = dlg.FileName;
                string dir      = Path.GetDirectoryName(objPath);
                string baseName = Path.GetFileNameWithoutExtension(objPath);
                string texDir   = Path.Combine(dir, baseName + "_textures");
 
                // --- Export textures as PNG ---
                string[] mtlKeys   = { "map_Kd","map_Bump","map_Ks","map_Pr","map_Pm","map_d" };
                string[] texLabels = { "Color","Normal","Specular","Roughness","Metallic","Opacity" };
                var exported = new Dictionary<int, string>();
                for (int s = 0; s < 6; s++)
                {
                    string src = model.TexPaths[s];
                    if (src == null || !File.Exists(src)) continue;
                    try
                    {
                        Directory.CreateDirectory(texDir);
                        string outName = texLabels[s] + ".png";
                        string ext2    = Path.GetExtension(src).ToLowerInvariant();
                        Bitmap bmp2 =
                            ext2 == ".dds" ? model.LoadDDSPublic(src) :
                            ext2 == ".tga" ? model.LoadTGAPublic(src) :
                            new Bitmap(src);
                        if (bmp2 != null)
                        {
                            bmp2.Save(Path.Combine(texDir, outName),
                                      System.Drawing.Imaging.ImageFormat.Png);
                            bmp2.Dispose();
                            exported[s] = outName;
                        }
                    }
                    catch { /* skip unreadable */ }
                }
 
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                string F(float v) => v.ToString("G6", ic);
 
                // --- Write MTL ---
                string mtlName = baseName + ".mtl";
                var mtl = new StringBuilder();
                mtl.AppendLine("# Black3DViewer export");
                mtl.AppendLine("newmtl mat0");
                mtl.AppendLine("Ka 1 1 1");  mtl.AppendLine("Kd 1 1 1");
                mtl.AppendLine("Ks 0 0 0");  mtl.AppendLine("d 1");
                foreach (var kv in exported)
                    mtl.AppendLine($"{mtlKeys[kv.Key]} {baseName}_textures/{kv.Value}");
                File.WriteAllText(Path.Combine(dir, mtlName), mtl.ToString(), Encoding.UTF8);
 
                // --- Write OBJ ---
                var sb = new StringBuilder();
                sb.AppendLine("# Black3DViewer export");
                sb.AppendLine($"mtllib {mtlName}");
                sb.AppendLine("g default");
                sb.AppendLine("usemtl mat0");
                sb.AppendLine();
                foreach (var v  in model.Vertices)   sb.AppendLine($"v  {F(v.X)} {F(v.Y)} {F(v.Z)}");
                sb.AppendLine();
                foreach (var uv in model.TexCoords)  sb.AppendLine($"vt {F(uv.X)} {F(uv.Y)}");
                sb.AppendLine();
                foreach (var n  in model.Normals)    sb.AppendLine($"vn {F(n.X)} {F(n.Y)} {F(n.Z)}");
                sb.AppendLine();
                bool hasUV2 = model.TexCoords.Count > 0;
                bool hasN2  = model.Normals.Count   > 0;
                foreach (var face in model.Faces)
                {
                    sb.Append("f");
                    for (int i = 0; i < 3; i++)
                    {
                        int vi = face.VI[i] + 1;
                        int ti = hasUV2 && face.TI != null && i < face.TI.Length ? face.TI[i]+1 : 0;
                        int ni = hasN2  && face.NI != null && i < face.NI.Length && face.NI[i]>=0 ? face.NI[i]+1 : 0;
                        if      (ti>0 && ni>0) sb.Append($" {vi}/{ti}/{ni}");
                        else if (ti>0)         sb.Append($" {vi}/{ti}");
                        else if (ni>0)         sb.Append($" {vi}//{ni}");
                        else                   sb.Append($" {vi}");
                    }
                    sb.AppendLine();
                }
                File.WriteAllText(objPath, sb.ToString(), Encoding.UTF8);
 
                string msg = $"Saved:\n  {Path.GetFileName(objPath)}\n  {mtlName}";
                if (exported.Count > 0)
                    msg += $"\n  {exported.Count} texture(s) in {baseName}_textures/";
                MessageBox.Show(msg, "Export Complete",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
 
        private void ResetCam()
        {
            if (model != null)
            {
                lookAt      = model.GetCenter();
                cachedCenter= lookAt;
                cachedSize  = model.GetSize();
                zoom        = -cachedSize * 1.8f;
            }
            else { zoom = -5f; lookAt = Vector3.Zero; }
            rotX = 0f; rotY = 0f;
            glControl.Invalidate();
        }
 
        private void OnMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if ((DateTime.Now - lastClick).TotalMilliseconds < 200) ResetCam();
                dragRot = true; lastMouse = e.Location; lastClick = DateTime.Now;
            }
            else if (e.Button == MouseButtons.Right)
            { dragPan = true; lastMouse = e.Location; }
            else if (e.Button == MouseButtons.Middle)
            { dragZoomMid = true; lastMouse = e.Location; }
        }
 
        private void OnMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)                                        dragRot = false;
            else if (e.Button == MouseButtons.Right)  dragPan     = false;
            else if (e.Button == MouseButtons.Middle) dragZoomMid = false;
        }
 
        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (dragRot)
            {
                // FIX: negate X delta ? dragging right rotates scene rightward (natural)
                rotY -= (e.X - lastMouse.X) * 0.5f;
                rotX += (e.Y - lastMouse.Y) * 0.5f;
                rotX  = Math.Max(ROT_MIN, Math.Min(ROT_MAX, rotX));
                lastMouse = e.Location;
                glControl.Invalidate();
            }
            else if (dragZoomMid)
            {
                float delta = (e.Y - lastMouse.Y) * 0.003f;
                zoom *= 1f + delta;
                zoom  = Math.Max(ZOOM_FAR, Math.Min(ZOOM_NEAR, zoom));
                lastMouse = e.Location;
                glControl.Invalidate();
            }
            else if (dragPan)
            {
                float ps = Math.Abs(zoom) * 0.001f;
                float dx = (e.X - lastMouse.X) * ps;
                float dy = (e.Y - lastMouse.Y) * ps;
 
                float ry = MathHelper.DegreesToRadians(rotY);
                float rx = MathHelper.DegreesToRadians(rotX);
                // Camera-space axes from arcball rotation (cross-product derived)
                // right = forward x world-up  =>  (cos ry, 0, -sin ry)
                // up    = right x forward      =>  (-sin ry sin rx, cos rx, -cos ry sin rx)
                var right = new Vector3((float)Math.Cos(ry), 0f, -(float)Math.Sin(ry));
                var up    = new Vector3(
                    -(float)(Math.Sin(ry) * Math.Sin(rx)),
                     (float)Math.Cos(rx),
                    -(float)(Math.Cos(ry) * Math.Sin(rx)));
 
                lookAt -= right * dx;
                lookAt += up    * dy;
                lastMouse = e.Location;
                glControl.Invalidate();
            }
        }
 
        private void OnWheel(object sender, MouseEventArgs e)
        {
            zoom *= e.Delta > 0 ? 0.9f : 1.1f;
            zoom  = Math.Max(ZOOM_FAR, Math.Min(ZOOM_NEAR, zoom));
            glControl.Invalidate();
        }
 
        private void OnLightPaint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int cx = lightPanel.Width / 2, cy = lightPanel.Height / 2, r = 70;
            g.FillEllipse(new SolidBrush(Color.FromArgb(245, 245, 245)), cx-r, cy-r, r*2, r*2);
            g.DrawEllipse(Pens.LightGray, cx-r, cy-r, r*2, r*2);
            int lx = (int)(cx + Math.Cos(lightAngle) * (r - 10));
            int ly = (int)(cy - Math.Sin(lightAngle) * (r - 10));
            g.DrawLine(new Pen(Color.FromArgb(0, 120, 215), 2), cx, cy, lx, ly);
            g.FillEllipse(Brushes.Gold, lx - 9, ly - 9, 18, 18);
            g.DrawEllipse(Pens.DarkGoldenrod, lx - 9, ly - 9, 18, 18);
        }
 
        private void UpdateLight(Point p)
        {
            int cx = lightPanel.Width / 2, cy = lightPanel.Height / 2;
            lightAngle = (float)Math.Atan2(cy - p.Y, p.X - cx);
            lightPanel.Invalidate();
            glControl.Invalidate();
        }
 
// =============================================================================
//  SECTION 10 - FILE LOADING & DRAG-DROP
// =============================================================================
 
        private static readonly HashSet<string> MODEL_EXT = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".obj", ".csv", ".stl", ".rip", ".nr", ".glb", ".dae", ".fbx" };
 
        private static readonly HashSet<string> TEX_EXT = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".png", ".jpg", ".jpeg", ".bmp", ".dds", ".tga" };
 
        private void OpenDialog()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title  = "Load 3D Model";
                dlg.Filter =
                    "All Supported (*.obj;*.csv;*.stl;*.rip;*.nr;*.glb;*.dae;*.fbx)|*.obj;*.csv;*.stl;*.rip;*.nr;*.glb;*.dae;*.fbx|" +
                    "Wavefront OBJ (*.obj)|*.obj|" +
                    "CSV Geometry (*.csv)|*.csv|" +
                    "STL (*.stl)|*.stl|" +
                    "NinjaRipper v1 (*.rip)|*.rip|" +
                    "NinjaRipper v2 (*.nr)|*.nr|" +
                    "glTF Binary (*.glb)|*.glb|" +
                    "Collada (*.dae)|*.dae|" +
                    "FBX ASCII (*.fbx)|*.fbx|" +
                    "All Files (*.*)|*.*";
                if (dlg.ShowDialog() == DialogResult.OK)
                    LoadFile(dlg.FileName);
            }
        }
 
        private void LoadFile(string path)
        {
            model?.Cleanup();
            model = new GPUModel();
            model.LoadFromFile(path);
 
            if (model.Faces.Count == 0) { model = null; return; }
 
            showCube     = false;
            shadeMode    = 2;
            texSlot      = 0;
            cachedCenter = model.GetCenter();
            cachedSize   = model.GetSize();
            lookAt       = cachedCenter;
            zoom         = -cachedSize * 1.8f;
            rotX = 0f; rotY = 0f;
            uvDirty = true;
            texPreviewBmp?.Dispose(); texPreviewBmp = null;  // reload on next paint
 
            loadedLabel.Text = Path.GetFileName(path);
            RefreshShade();
            RefreshTexBtns();
            RefreshTexBtnEnabled();
            UpdateStatLabels();
 
            // Set up file-change watchers for every loaded texture slot
            for (int ws = 0; ws < 6; ws++)
                SetupTexWatcher(ws, model.TexPaths[ws]);
 
            previewPanel.Invalidate();
            glControl.Invalidate();
        }
 
        private void OnDrop(object sender, DragEventArgs e)
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files == null || files.Length == 0) return;
            string f   = files[0];
            string ext = Path.GetExtension(f);
            if      (MODEL_EXT.Contains(ext))               LoadFile(f);
            else if (TEX_EXT.Contains(ext) && model != null)
            {
                model.LoadTexture(f, texSlot);
                SetupTexWatcher(texSlot, f);
                texPreviewBmp?.Dispose(); texPreviewBmp = null;
                RefreshTexBtnEnabled();
                UpdateNoTex();
                previewPanel.Invalidate();
                glControl.Invalidate();
            }
        }
 
// =============================================================================
//  SECTION 11 - UI STATE HELPERS
// =============================================================================
 
        private void SetSlot(int s) { texSlot = s; RefreshTexBtns(); UpdateNoTex(); glControl.Invalidate(); }
 
        private void RefreshShade()
        {
            solidBtn.BackColor = shadeMode == 0 ? PRESS : IDLE;
            wireBtn.BackColor  = shadeMode == 1 ? PRESS : IDLE;
            texBtn.BackColor   = shadeMode == 2 ? PRESS : IDLE;
            RefreshTexBtnEnabled();
            UpdateNoTex();
            glControl.Invalidate();
        }
 
        private void RefreshTexBtns()
        {
            colBtn.BackColor  = texSlot == 0 ? PRESS : IDLE;
            nrmBtn.BackColor  = texSlot == 1 ? PRESS : IDLE;
            specBtn.BackColor = texSlot == 2 ? PRESS : IDLE;
            roughBtn.BackColor= texSlot == 3 ? PRESS : IDLE;
            metBtn.BackColor  = texSlot == 4 ? PRESS : IDLE;
            opqBtn.BackColor  = texSlot == 5 ? PRESS : IDLE;
        }
 
        private void RefreshTexBtnEnabled()
        {
            bool en = shadeMode == 2 && model != null;
            colBtn.Enabled = nrmBtn.Enabled = specBtn.Enabled =
            roughBtn.Enabled = metBtn.Enabled = opqBtn.Enabled = en;
            if (!en)
            {
                var grey = Color.FromArgb(215, 215, 215);
                colBtn.BackColor = nrmBtn.BackColor = specBtn.BackColor =
                roughBtn.BackColor = metBtn.BackColor = opqBtn.BackColor = grey;
            }
        }
 
        private void UpdateNoTex()
        {
            if (model == null) { noTexLabel.Visible = false; return; }
            noTexLabel.Visible = shadeMode == 2 && !model.HasTex(texSlot);
        }
 
        private void UpdateStatLabels()
        {
            if (model != null)
            {
                vertLabel.Text = $"Vertices: {model.Vertices.Count}";
                triLabel.Text  = $"Triangles: {model.Faces.Count}";
                edgeLabel.Text = $"Edges: {model.EdgeCount}";
            }
            else
            {
                vertLabel.Text = "Vertices: 8";
                triLabel.Text  = "Triangles: 12";
                edgeLabel.Text = "Edges: 18";
            }
        }
 
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            for (int ws = 0; ws < 6; ws++) { try { _texWatchers[ws]?.Dispose(); } catch { } }
            _themeTimer?.Dispose();
            model?.Cleanup();
            uvCache?.Dispose();
            texPreviewBmp?.Dispose();
            if (cubeVAO != 0) GL.DeleteVertexArray(cubeVAO);
            if (cubeVBO != 0) GL.DeleteBuffer(cubeVBO);
        }
 
// =============================================================================
//  TEXTURE FILE WATCHER - auto-reload textures on external edits
// =============================================================================
 
        private void SetupTexWatcher(int slot, string path)
        {
            // Dispose any previous watcher for this slot
            try { _texWatchers[slot]?.Dispose(); } catch { }
            _texWatchers[slot] = null;
 
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            string dir  = Path.GetDirectoryName(path);
            string file = Path.GetFileName(path);
            if (string.IsNullOrEmpty(dir)) return;
 
            var w = new System.IO.FileSystemWatcher(dir, file)
            {
                NotifyFilter        = System.IO.NotifyFilters.LastWrite | System.IO.NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            int capturedSlot = slot;
            w.Changed += (_, __) =>
            {
                // Debounce: only act if >400 ms since last reload for this slot
                if ((DateTime.Now - _lastReload[capturedSlot]).TotalMilliseconds < 400) return;
                _lastReload[capturedSlot] = DateTime.Now;
                System.Threading.Thread.Sleep(300); // let the app finish writing
                if (IsDisposed) return;
                try { BeginInvoke((Action)(() => ReloadTextureSlot(capturedSlot))); } catch { }
            };
            _texWatchers[slot] = w;
        }
 
        private void ReloadTextureSlot(int slot)
        {
            if (model == null) return;
            string path = model.TexPaths[slot];
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
 
            glControl.MakeCurrent();
 
            // Delete the old GPU texture before uploading the new one
            int oldId = model.GetTexId(slot);
            if (oldId >= 0) GL.DeleteTexture(oldId);
 
            // Reload from disk
            model.LoadTexture(path, slot);
 
            // Invalidate preview if colour map changed
            if (slot == 0) { texPreviewBmp?.Dispose(); texPreviewBmp = null; }
 
            RefreshTexBtnEnabled();
            UpdateNoTex();
            previewPanel.Invalidate();
            glControl.Invalidate();
        }
 
    } // end Viewer3DForm
 
// ============================================================================================================================
//  ________ ___  ___       ________          ________ ________  ________  _____   ____   ________  __________ ________      ||
// |\  _____\\  \|\  \     |\   ____|        |\  _____\\   __  \|\   __  \|\   _\  \_   \|\   __  \|\___   ___\\   ____\     ||
// \ \  \__/\ \  \ \  \    \ \  \____        \ \  \__/\ \  \ \  \ \  \_\  \ \  \\\__\ \  \ \  \_\  \|___ \  \_\ \  \___ _    ||
//  \ \   __\\ \  \ \  \    \ \  \____|       \ \   __\\ \  \ \  \ \   _  _\ \  \\|__| \  \ \   __  \   \ \  \ \ \_____  \   ||
//   \ \  \_/ \ \  \ \  \____\ \  \_____       \ \  \_/ \ \  \_\  \ \  \\  \\ \  \    \ \  \ \  \\\  \   \ \  \ \|____|\  \  ||
//    \ \__\   \ \__\ \_______\ \_______\       \ \__\   \ \_______\ \__\\ _\\ \__\    \ \__\ \__\\\__\   \ \__\  ____\_\  \ ||
//     \|__|    \|__|\|_______|\|________|       \|__|    \|_______|\|__|\|__|||__|     \|__|\|__|\|__|    \|__| |\_________\||
//                                                                                                               \|_________|||
// ============================================================================================================================
//  THE FORMAT LOADERS
//  All return: (verts, uvs, norms, faces, boundsMin, boundsMax)
//  All use 0-based indices internally.
// ============================================================================================================================
 
    // --------------------------- OBJ - Wavefront ------------------------------------
    public static class ObjLoader
    {
        public static (List<Vector3> v, List<Vector2> uv, List<Vector3> n, List<MeshFace> f, Vector3 bMin, Vector3 bMax)
            Load(string path)
        {
            var verts  = new List<Vector3>();
            var uvs    = new List<Vector2>();
            var norms  = new List<Vector3>();
            var faces  = new List<MeshFace>();
            var bMin   = new Vector3( float.MaxValue,  float.MaxValue,  float.MaxValue);
            var bMax   = new Vector3(-float.MaxValue, -float.MaxValue, -float.MaxValue);
 
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var p = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 2) continue;
 
                if (p[0] == "v" && p.Length >= 4)
                {
                    // Preserve original axis swap from user's code: Y?Z, flip Z
                    // This converts Z-up (NinjaRipper/DX style) to Y-up (OpenGL).
                    var vert = new Vector3(F(p[1]), F(p[3]), -F(p[2]));
                    verts.Add(vert);
                    bMin = Vector3.ComponentMin(bMin, vert);
                    bMax = Vector3.ComponentMax(bMax, vert);
                }
                else if (p[0] == "vt" && p.Length >= 3)
                    uvs.Add(new Vector2(F(p[1]), F(p[2])));
                else if (p[0] == "vn" && p.Length >= 4)
                    norms.Add(new Vector3(F(p[1]), F(p[3]), -F(p[2])));
                else if (p[0] == "f" && p.Length >= 4)
                {
                    int n = p.Length - 1;
                    var vi = new int[n]; var ti = new int[n]; var ni = new int[n];
                    for (int i = 0; i < n; i++)
                    {
                        var idx = p[i + 1].Split('/');
                        vi[i] = int.Parse(idx[0]) - 1;
                        ti[i] = idx.Length > 1 && idx[1].Length > 0 ? int.Parse(idx[1]) - 1 : 0;
                        ni[i] = idx.Length > 2 && idx[2].Length > 0 ? int.Parse(idx[2]) - 1 : -1;
                    }
                    // Fan-triangulate any n-gon
                    for (int i = 1; i < n - 1; i++)
                        faces.Add(new MeshFace(
                            new[] { vi[0], vi[i], vi[i + 1] },
                            new[] { ti[0], ti[i], ti[i + 1] },
                            new[] { ni[0], ni[i], ni[i + 1] }));
                }
            }
            return (verts, uvs, norms, faces, bMin, bMax);
        }
 
        static float F(string s)
        {
            float.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float v);
            return v;
        }
    }
 
    // --------------------------- CSV - RenderDoc ------------------------------------
    // Layout matches CSV-2-OBJ.py: columns [2,3,4]=X,Y,Z  [5,6]=U,V
    // Every 3 rows form one triangle.
    public static class CsvLoader
    {
        public static (List<Vector3> v, List<Vector2> uv, List<Vector3> n, List<MeshFace> f, Vector3 bMin, Vector3 bMax)
            Load(string path)
        {
            var verts = new List<Vector3>(); var uvs = new List<Vector2>();
            var norms = new List<Vector3>(); var faces = new List<MeshFace>();
            var bMin  = new Vector3( float.MaxValue,  float.MaxValue,  float.MaxValue);
            var bMax  = new Vector3(-float.MaxValue, -float.MaxValue, -float.MaxValue);
 
            var lines = File.ReadAllLines(path);
            int rowInFace = 0;
 
            for (int li = 1; li < lines.Length; li++)  // skip header row
            {
                var p = lines[li].Split(',');
                if (p.Length < 7) continue;
 
                float x = F(p[2]), y = F(p[3]), z = -F(p[4]);   // mirror Z
                float u = F(p[5]), vv = 1f - F(p[6]);            // flip V
                var vert = new Vector3(x, y, z);
                verts.Add(vert);
                uvs.Add(new Vector2(u, vv));
                bMin = Vector3.ComponentMin(bMin, vert);
                bMax = Vector3.ComponentMax(bMax, vert);
                rowInFace++;
 
                if (rowInFace == 3)
                {
                    int b = verts.Count - 3;
                    faces.Add(new MeshFace(new[] { b, b + 1, b + 2 },
                                           new[] { b, b + 1, b + 2 },
                                           new[] { -1, -1, -1 }));
                    rowInFace = 0;
                }
            }
            return (verts, uvs, norms, faces, bMin, bMax);
        }
 
        static float F(string s)
        {
            float.TryParse(s.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float v);
            return v;
        }
    }
 
    // --------------------------- STL - Stereolithography ------------------------------------
    public static class StlLoader
    {
        public static (List<Vector3> v, List<Vector2> uv, List<Vector3> n, List<MeshFace> f, Vector3 bMin, Vector3 bMax)
            Load(string path)
        {
            // Detect: binary STL files can start with "solid" too, so check for "facet" keyword
            bool isAscii = false;
            try
            {
                using (var sr = new StreamReader(path))
                {
                    string first = sr.ReadLine() ?? "";
                    string second = sr.ReadLine() ?? "";
                    isAscii = first.TrimStart().StartsWith("solid") &&
                              second.TrimStart().StartsWith("facet");
                }
            }
            catch { }
 
            return isAscii ? LoadAscii(path) : LoadBinary(path);
        }
 
        static (List<Vector3>, List<Vector2>, List<Vector3>, List<MeshFace>, Vector3, Vector3) LoadBinary(string path)
        {
            var verts = new List<Vector3>(); var uvs = new List<Vector2>();
            var norms = new List<Vector3>(); var faces = new List<MeshFace>();
            var bMin  = new Vector3( float.MaxValue,  float.MaxValue,  float.MaxValue);
            var bMax  = new Vector3(-float.MaxValue, -float.MaxValue, -float.MaxValue);
 
            using (var br = new BinaryReader(File.OpenRead(path)))
            {
                br.ReadBytes(80);             // 80-byte header
                uint count = br.ReadUInt32(); // triangle count
                for (uint i = 0; i < count; i++)
                {
                    float nx = br.ReadSingle(), ny = br.ReadSingle(), nz = br.ReadSingle();
                    int ni = norms.Count;
                    norms.Add(new Vector3(nx, ny, nz));
                    int vi = verts.Count;
                    for (int j = 0; j < 3; j++)
                    {
                        float x = br.ReadSingle(), y = br.ReadSingle(), z = br.ReadSingle();
                        var vert = new Vector3(x, y, z);
                        verts.Add(vert); uvs.Add(Vector2.Zero);
                        bMin = Vector3.ComponentMin(bMin, vert);
                        bMax = Vector3.ComponentMax(bMax, vert);
                    }
                    br.ReadUInt16(); // attribute byte count
                    faces.Add(new MeshFace(new[] { vi, vi + 1, vi + 2 },
                                           new[] { vi, vi + 1, vi + 2 },
                                           new[] { ni, ni, ni }));
                }
            }
            return (verts, uvs, norms, faces, bMin, bMax);
        }
 
        static (List<Vector3>, List<Vector2>, List<Vector3>, List<MeshFace>, Vector3, Vector3) LoadAscii(string path)
        {
            var verts = new List<Vector3>(); var uvs = new List<Vector2>();
            var norms = new List<Vector3>(); var faces = new List<MeshFace>();
            var bMin  = new Vector3( float.MaxValue,  float.MaxValue,  float.MaxValue);
            var bMax  = new Vector3(-float.MaxValue, -float.MaxValue, -float.MaxValue);
 
            Vector3 curNorm = Vector3.UnitY;
            int fvCount = 0, vi = 0;
 
            foreach (var raw in File.ReadAllLines(path))
            {
                var p = raw.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 0) continue;
 
                if (p[0] == "facet" && p.Length >= 5)
                {
                    curNorm = new Vector3(F(p[2]), F(p[3]), F(p[4]));
                    fvCount = 0; vi = verts.Count;
                }
                else if (p[0] == "vertex" && p.Length >= 4)
                {
                    var vert = new Vector3(F(p[1]), F(p[2]), F(p[3]));
                    verts.Add(vert); uvs.Add(Vector2.Zero);
                    bMin = Vector3.ComponentMin(bMin, vert);
                    bMax = Vector3.ComponentMax(bMax, vert);
                    fvCount++;
                }
                else if (p[0] == "endfacet" && fvCount == 3)
                {
                    int ni = norms.Count; norms.Add(curNorm);
                    faces.Add(new MeshFace(new[] { vi, vi + 1, vi + 2 },
                                           new[] { vi, vi + 1, vi + 2 },
                                           new[] { ni, ni, ni }));
                }
            }
            return (verts, uvs, norms, faces, bMin, bMax);
        }
 
        static float F(string s)
        {
            float.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float v);
            return v;
        }
    }
 
    // --------------------------- RIP - NinjaRipper ------------------
    public static class NR1Loader
    {
        private static readonly Dictionary<int, int> UvOffsets =
            new Dictionary<int, int> { { 184, 60 }, { 160, 52 }, { 56, 48 }, { 20, 12 } };
 
        // posOff = byte offset in vertex record for XYZ floats (usually 0).
        // uvOff  = byte offset for UV; pass -1 to auto-detect from stride table.
        public static (List<Vector3> v, List<Vector2> uv, List<Vector3> n, List<MeshFace> f, Vector3 bMin, Vector3 bMax)
            Load(string path, int posOff = 0, int uvOff = -1)
        {
            var verts = new List<Vector3>(); var uvs = new List<Vector2>();
            var norms = new List<Vector3>(); var faces = new List<MeshFace>();
            var bMin  = new Vector3( float.MaxValue,  float.MaxValue,  float.MaxValue);
            var bMax  = new Vector3(-float.MaxValue, -float.MaxValue, -float.MaxValue);
 
            byte[] data = File.ReadAllBytes(path);
 
            int faceCount = BitConverter.ToInt32(data, 0x08);
            int vertCount = BitConverter.ToInt32(data, 0x0C);
            int stride    = BitConverter.ToInt32(data, 0x10);
 
            // Find face-list start via sentinel pattern 0,1,2
            byte[] pat = { 0,0,0,0, 1,0,0,0, 2,0,0,0 };
            int faceStart = -1;
            for (int i = 0; i < data.Length - pat.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < pat.Length; j++) if (data[i + j] != pat[j]) { ok = false; break; }
                if (ok) { faceStart = i; break; }
            }
            if (faceStart < 0) throw new Exception("Face start pattern not found in .rip file.");
 
            // Read faces
            for (int i = 0; i < faceCount; i++)
            {
                int off = faceStart + i * 12;
                if (off + 12 > data.Length) break;
                int i0 = BitConverter.ToInt32(data, off);
                int i1 = BitConverter.ToInt32(data, off + 4);
                int i2 = BitConverter.ToInt32(data, off + 8);
                faces.Add(new MeshFace(new[] { i0, i1, i2 },
                                       new[] { i0, i1, i2 },
                                       new[] { i0, i1, i2 }));
            }
 
            int vdStart = faceStart + faceCount * 12;
            if (uvOff < 0) UvOffsets.TryGetValue(stride, out uvOff); // auto-detect
            int normOff = posOff + 12; // normals follow position by default
 
            for (int i = 0; i < vertCount; i++)
            {
                int vOff = vdStart + i * stride;
                if (vOff + posOff + 12 > data.Length) break;
 
                float x = BitConverter.ToSingle(data, vOff + posOff);
                float y = BitConverter.ToSingle(data, vOff + posOff + 4);
                float z = BitConverter.ToSingle(data, vOff + posOff + 8);
 
                float nx = 0, ny = 1, nz = 0;
                if (vOff + normOff + 12 <= data.Length)
                {
                    nx = BitConverter.ToSingle(data, vOff + normOff);
                    ny = BitConverter.ToSingle(data, vOff + normOff + 4);
                    nz = BitConverter.ToSingle(data, vOff + normOff + 8);
                }
 
                var vert = new Vector3(x, y, z);
                verts.Add(vert);
                norms.Add(new Vector3(nx, ny, nz));
                bMin = Vector3.ComponentMin(bMin, vert);
                bMax = Vector3.ComponentMax(bMax, vert);
 
                if (uvOff > 0 && vOff + uvOff + 8 <= data.Length)
                {
                    float u  = BitConverter.ToSingle(data, vOff + uvOff);
                    float vv = 1f - BitConverter.ToSingle(data, vOff + uvOff + 4);
                    uvs.Add(new Vector2(u, vv));
                }
                else uvs.Add(Vector2.Zero);
            }
            return (verts, uvs, norms, faces, bMin, bMax);
        }
    }
 
    // --------------------------- NR - NinjaRipper 2 -------------------
    public static class NR2Loader
    {
        private const uint MAGIC = 0x5049524E; // "NRIP"
        private const uint TAG_VERT = 0x54524556;
        private const uint TAG_INDX = 0x58444E49;
 
        public static (List<Vector3> v, List<Vector2> uv, List<Vector3> n, List<MeshFace> f, Vector3 bMin, Vector3 bMax)
            Load(string path)
        {
            var verts = new List<Vector3>(); var uvs = new List<Vector2>();
            var norms = new List<Vector3>(); var faces = new List<MeshFace>();
            var bMin  = new Vector3( float.MaxValue,  float.MaxValue,  float.MaxValue);
            var bMax  = new Vector3(-float.MaxValue, -float.MaxValue, -float.MaxValue);
 
            byte[] data = File.ReadAllBytes(path);
            if (data.Length < 16 || BitConverter.ToUInt32(data, 0) != MAGIC)
                throw new Exception("Not a valid NinjaRipper 2 file (bad magic).");
 
            // Collect VERT and INDX chunks
            var vertChunks = new List<(int dataOff, int dataLen)>();
            int indxOff = -1, indxLen = 0;
 
            int pos = 16; // skip header (magic+version+reserved)
            while (pos + 12 <= data.Length)
            {
                int  rawSize = BitConverter.ToInt32(data, pos);
                uint tag     = BitConverter.ToUInt32(data, pos + 4);
                int  dOff    = pos + 12;
                int  dLen    = rawSize - 12;
                if (dLen < 0) break;
 
                if (tag == TAG_VERT) vertChunks.Add((dOff, dLen));
                else if (tag == TAG_INDX) { indxOff = dOff; indxLen = dLen; }
 
                pos += rawSize;
            }
 
            if (vertChunks.Count == 0)
                throw new Exception("No VERT chunk found in .nr file.");
 
            // Prefer world-space chunk (index 1) if present
            var (vOff, _) = vertChunks[vertChunks.Count > 1 ? 1 : 0];
 
            int vertCount = BitConverter.ToInt32(data, vOff);
            int vertSize  = BitConverter.ToInt32(data, vOff + 4);
            int vd        = vOff + 8;
 
            for (int i = 0; i < vertCount; i++, vd += vertSize)
            {
                if (vd + 12 > data.Length) break;
                float x = BitConverter.ToSingle(data, vd);
                float y = BitConverter.ToSingle(data, vd + 4);
                float z = BitConverter.ToSingle(data, vd + 8);
                var vert = new Vector3(x, y, z);
                verts.Add(vert); uvs.Add(Vector2.Zero);
                bMin = Vector3.ComponentMin(bMin, vert);
                bMax = Vector3.ComponentMax(bMax, vert);
            }
 
            if (indxOff >= 0)
            {
                int indexCount = BitConverter.ToInt32(data, indxOff);
                int id = indxOff + 8;
                for (int i = 0; i + 2 < indexCount; i += 3, id += 12)
                {
                    if (id + 12 > data.Length) break;
                    int i0 = BitConverter.ToInt32(data, id);
                    int i1 = BitConverter.ToInt32(data, id + 4);
                    int i2 = BitConverter.ToInt32(data, id + 8);
                    if (i0 < verts.Count && i1 < verts.Count && i2 < verts.Count)
                        faces.Add(new MeshFace(new[] { i0, i1, i2 },
                                               new[] { 0, 0, 0 },
                                               new[] { -1, -1, -1 }));
                }
            }
            return (verts, uvs, norms, faces, bMin, bMax);
        }
    }
 
    // --------------------------- GLB - Binary glTF 2.0 -----------------
    public static class GlbLoader
    {
        public static (List<Vector3> v, List<Vector2> uv, List<Vector3> n, List<MeshFace> f, Vector3 bMin, Vector3 bMax)
            Load(string path)
        {
            var verts = new List<Vector3>(); var uvs = new List<Vector2>();
            var norms = new List<Vector3>(); var faces = new List<MeshFace>();
            var bMin  = new Vector3( float.MaxValue,  float.MaxValue,  float.MaxValue);
            var bMax  = new Vector3(-float.MaxValue, -float.MaxValue, -float.MaxValue);
 
            byte[] data = File.ReadAllBytes(path);
            if (data.Length < 12 || BitConverter.ToUInt32(data, 0) != 0x46546C67)
                throw new Exception("Not a valid GLB file (bad magic).");
 
            // GLB header: 12 bytes. JSON chunk header at offset 12.
            int jsonLen = BitConverter.ToInt32(data, 12);
            // chunk type at offset 16 should be 0x4E4F534A (JSON) ? trust it
            string json = Encoding.UTF8.GetString(data, 20, jsonLen).TrimEnd('\0', ' ');
 
            // BIN chunk immediately follows JSON chunk: 20+jsonLen is start of bin chunk header
            byte[] bin = Array.Empty<byte>();
            int binHeaderOff = 20 + jsonLen;
            if (binHeaderOff + 8 <= data.Length)
            {
                int binLen = BitConverter.ToInt32(data, binHeaderOff);
                // chunk type at binHeaderOff+4 should be 0x004E4942 (BIN)
                bin = new byte[binLen];
                int binDataOff = binHeaderOff + 8;
                Array.Copy(data, binDataOff, bin, 0, Math.Min(binLen, data.Length - binDataOff));
            }
 
            // Parse JSON for attribute accessor indices
            int posAcc = JsonInt(json, "\"POSITION\"");
            int nrmAcc = JsonInt(json, "\"NORMAL\"");
            int uvAcc  = JsonInt(json, "\"TEXCOORD_0\"");
            int idxAcc = JsonInt(json, "\"indices\"");
 
            var accs  = ParseAccessors(json);
            var bvs   = ParseBufferViews(json);
 
            // Read typed data from bin via accessor + bufferView
            verts = ReadVec3(bin, accs, bvs, posAcc);
            norms = ReadVec3(bin, accs, bvs, nrmAcc);
            uvs   = ReadVec2(bin, accs, bvs, uvAcc);
 
            while (uvs.Count < verts.Count) uvs.Add(Vector2.Zero);
 
            foreach (var v in verts) { bMin = Vector3.ComponentMin(bMin, v); bMax = Vector3.ComponentMax(bMax, v); }
 
            // Read indices
            if (idxAcc >= 0 && idxAcc < accs.Count)
            {
                var ac = accs[idxAcc];
                var bv = bvs[ac.BufView];
                int off  = bv.ByteOffset + ac.ByteOffset;
                int step = ac.CompType == 5125 ? 4 : ac.CompType == 5123 ? 2 : 1;
                for (int i = 0; i + 2 < ac.Count; i += 3)
                {
                    int i0 = ReadIdx(bin, off + i * step, ac.CompType);
                    int i1 = ReadIdx(bin, off + (i + 1) * step, ac.CompType);
                    int i2 = ReadIdx(bin, off + (i + 2) * step, ac.CompType);
                    if (i0 < verts.Count && i1 < verts.Count && i2 < verts.Count)
                        faces.Add(new MeshFace(
                            new[] { i0, i1, i2 },
                            new[] { i0 < uvs.Count ? i0 : 0,  i1 < uvs.Count ? i1 : 0,  i2 < uvs.Count ? i2 : 0 },
                            new[] { i0 < norms.Count ? i0 : -1, i1 < norms.Count ? i1 : -1, i2 < norms.Count ? i2 : -1 }));
                }
            }
            return (verts, uvs, norms, faces, bMin, bMax);
        }
 
        // -- minimal accessor / bufferView structs --
        private struct GlbAcc  { public int BufView, ByteOffset, CompType, Count; }
        private struct GlbBV   { public int ByteOffset, ByteStride; }
 
        private static List<GlbAcc> ParseAccessors(string json)
        {
            var list = new List<GlbAcc>();
            int pos = json.IndexOf("\"accessors\"", StringComparison.Ordinal);
            if (pos < 0) return list;
            pos = json.IndexOf('[', pos); if (pos < 0) return list;
            int end = MatchBracket(json, pos, '[', ']');
            foreach (var obj in SplitObjects(json, pos + 1, end))
                list.Add(new GlbAcc {
                    BufView    = ObjInt(obj, "bufferView"),
                    ByteOffset = ObjInt(obj, "byteOffset"),
                    CompType   = ObjInt(obj, "componentType"),
                    Count      = ObjInt(obj, "count") });
            return list;
        }
 
        private static List<GlbBV> ParseBufferViews(string json)
        {
            var list = new List<GlbBV>();
            int pos = json.IndexOf("\"bufferViews\"", StringComparison.Ordinal);
            if (pos < 0) return list;
            pos = json.IndexOf('[', pos); if (pos < 0) return list;
            int end = MatchBracket(json, pos, '[', ']');
            foreach (var obj in SplitObjects(json, pos + 1, end))
                list.Add(new GlbBV {
                    ByteOffset = ObjInt(obj, "byteOffset"),
                    ByteStride = ObjInt(obj, "byteStride") });
            return list;
        }
 
        private static List<Vector3> ReadVec3(byte[] bin, List<GlbAcc> accs, List<GlbBV> bvs, int accIdx)
        {
            var r = new List<Vector3>();
            if (accIdx < 0 || accIdx >= accs.Count || bvs.Count == 0) return r;
            var ac = accs[accIdx]; if (ac.BufView >= bvs.Count) return r;
            var bv = bvs[ac.BufView];
            int stride = bv.ByteStride > 0 ? bv.ByteStride : 12;
            int off = bv.ByteOffset + ac.ByteOffset;
            for (int i = 0; i < ac.Count; i++, off += stride)
            {
                if (off + 12 > bin.Length) break;
                r.Add(new Vector3(BitConverter.ToSingle(bin, off),
                                  BitConverter.ToSingle(bin, off + 4),
                                  BitConverter.ToSingle(bin, off + 8)));
            }
            return r;
        }
 
        private static List<Vector2> ReadVec2(byte[] bin, List<GlbAcc> accs, List<GlbBV> bvs, int accIdx)
        {
            var r = new List<Vector2>();
            if (accIdx < 0 || accIdx >= accs.Count || bvs.Count == 0) return r;
            var ac = accs[accIdx]; if (ac.BufView >= bvs.Count) return r;
            var bv = bvs[ac.BufView];
            int stride = bv.ByteStride > 0 ? bv.ByteStride : 8;
            int off = bv.ByteOffset + ac.ByteOffset;
            for (int i = 0; i < ac.Count; i++, off += stride)
            {
                if (off + 8 > bin.Length) break;
                r.Add(new Vector2(BitConverter.ToSingle(bin, off),
                                  BitConverter.ToSingle(bin, off + 4)));
            }
            return r;
        }
 
        private static int ReadIdx(byte[] b, int off, int ct)
        {
            if (off < 0 || off >= b.Length) return 0;
            if (ct == 5125 && off + 4 <= b.Length) return BitConverter.ToInt32(b, off);
            if (ct == 5123 && off + 2 <= b.Length) return BitConverter.ToUInt16(b, off);
            return b[off];
        }
 
        // -- tiny JSON helpers --
        private static int JsonInt(string json, string key)
        {
            int pos = json.IndexOf(key, StringComparison.Ordinal); if (pos < 0) return -1;
            pos = json.IndexOf(':', pos) + 1;
            while (pos < json.Length && (json[pos] == ' ' || json[pos] == '\n' || json[pos] == '\r' || json[pos] == '\t')) pos++;
            int end = pos;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-')) end++;
            return end > pos && int.TryParse(json.Substring(pos, end - pos), out int v) ? v : -1;
        }
 
        private static int ObjInt(string obj, string key)
        {
            int pos = obj.IndexOf("\"" + key + "\"", StringComparison.Ordinal); if (pos < 0) return 0;
            pos = obj.IndexOf(':', pos) + 1;
            while (pos < obj.Length && (obj[pos] == ' ' || obj[pos] == '\n')) pos++;
            int end = pos;
            while (end < obj.Length && (char.IsDigit(obj[end]) || obj[end] == '-')) end++;
            return end > pos && int.TryParse(obj.Substring(pos, end - pos), out int v) ? v : 0;
        }
 
        private static int MatchBracket(string s, int start, char open, char close)
        {
            int depth = 0;
            for (int i = start; i < s.Length; i++)
            {
                if      (s[i] == open)  depth++;
                else if (s[i] == close) { if (--depth == 0) return i; }
            }
            return s.Length - 1;
        }
 
        private static IEnumerable<string> SplitObjects(string s, int from, int to)
        {
            int depth = 0, start = -1;
            for (int i = from; i < to && i < s.Length; i++)
            {
                if (s[i] == '{') { if (depth++ == 0) start = i; }
                else if (s[i] == '}') { if (--depth == 0 && start >= 0) yield return s.Substring(start, i - start + 1); }
            }
        }
    }
 
    // --------------------------- DAE - Collada --------------------------
    public static class DaeLoader
    {
        public static (List<Vector3> v, List<Vector2> uv, List<Vector3> n, List<MeshFace> f, Vector3 bMin, Vector3 bMax)
            Load(string path)
        {
            var verts = new List<Vector3>(); var uvs = new List<Vector2>();
            var norms = new List<Vector3>(); var faces = new List<MeshFace>();
            var bMin  = new Vector3( float.MaxValue,  float.MaxValue,  float.MaxValue);
            var bMax  = new Vector3(-float.MaxValue, -float.MaxValue, -float.MaxValue);
 
            var doc = new XmlDocument();
            doc.Load(path);
 
            // Find <mesh> regardless of namespace
            XmlElement meshEl = FindFirst(doc.DocumentElement, "mesh");
            if (meshEl == null) return (verts, uvs, norms, faces, bMin, bMax);
 
            // Collect all <source> arrays
            var sources = new Dictionary<string, float[]>();
            foreach (XmlElement src in FindAll(meshEl, "source"))
            {
                string id  = src.GetAttribute("id");
                var faEl   = FindFirst(src, "float_array");
                if (id == null || faEl == null) continue;
                var raw = faEl.InnerText.Trim().Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                var fl  = new float[raw.Length];
                for (int i = 0; i < raw.Length; i++) F(raw[i], out fl[i]);
                sources["#" + id] = fl;
            }
 
            // Find vertex position source via <vertices><input POSITION>
            string posId = null;
            var vertsEl = FindFirst(meshEl, "vertices");
            if (vertsEl != null)
            {
                string vid = vertsEl.GetAttribute("id");
                foreach (XmlElement inp in FindAll(vertsEl, "input"))
                    if (inp.GetAttribute("semantic") == "POSITION") posId = inp.GetAttribute("source");
                // <triangles> uses "#vertices_id" to reference the vertex block
                if (vid != null) sources["#" + vid] = posId != null && sources.ContainsKey(posId) ? sources[posId] : new float[0];
            }
 
            // Find triangle / polylist node
            XmlElement triEl = FindFirst(meshEl, "triangles") ?? FindFirst(meshEl, "polylist");
            if (triEl == null) return (verts, uvs, norms, faces, bMin, bMax);
 
            string normId = null, uvId = null;
            int posOff = 0, normOff = 1, uvOff = 2, inputSpan = 1;
 
            foreach (XmlElement inp in FindAll(triEl, "input"))
            {
                string sem = inp.GetAttribute("semantic");
                string src = inp.GetAttribute("source");
                int.TryParse(inp.GetAttribute("offset"), out int off);
                inputSpan = Math.Max(inputSpan, off + 1);
                if (sem == "VERTEX" || sem == "POSITION") { posId = posId ?? src; posOff = off; }
                else if (sem == "NORMAL")   { normId = src; normOff = off; }
                else if (sem == "TEXCOORD" && uvId == null) { uvId = src; uvOff = off; }
            }
 
            // Build arrays
            if (posId != null && sources.ContainsKey(posId))
            {
                var pf = sources[posId];
                for (int i = 0; i + 2 < pf.Length; i += 3)
                {
                    var vert = new Vector3(pf[i], pf[i + 1], pf[i + 2]);
                    verts.Add(vert);
                    bMin = Vector3.ComponentMin(bMin, vert);
                    bMax = Vector3.ComponentMax(bMax, vert);
                }
            }
 
            if (normId != null && sources.ContainsKey(normId))
            {
                var nf = sources[normId];
                for (int i = 0; i + 2 < nf.Length; i += 3)
                    norms.Add(new Vector3(nf[i], nf[i + 1], nf[i + 2]));
            }
 
            if (uvId != null && sources.ContainsKey(uvId))
            {
                var uf = sources[uvId];
                for (int i = 0; i + 1 < uf.Length; i += 2)
                    uvs.Add(new Vector2(uf[i], uf[i + 1]));
            }
 
            // Parse face indices
            var pEl = FindFirst(triEl, "p");
            if (pEl != null)
            {
                var idx = pEl.InnerText.Trim().Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                int stride = inputSpan;
                for (int i = 0; i + stride * 3 - 1 < idx.Length; i += stride * 3)
                {
                    int vi0 = int.Parse(idx[i + posOff]);
                    int vi1 = int.Parse(idx[i + stride   + posOff]);
                    int vi2 = int.Parse(idx[i + stride*2 + posOff]);
                    int ni0 = normId != null ? int.Parse(idx[i + normOff]) : -1;
                    int ni1 = normId != null ? int.Parse(idx[i + stride   + normOff]) : -1;
                    int ni2 = normId != null ? int.Parse(idx[i + stride*2 + normOff]) : -1;
                    int ti0 = uvId != null ? int.Parse(idx[i + uvOff]) : 0;
                    int ti1 = uvId != null ? int.Parse(idx[i + stride   + uvOff]) : 0;
                    int ti2 = uvId != null ? int.Parse(idx[i + stride*2 + uvOff]) : 0;
                    if (vi0 < verts.Count && vi1 < verts.Count && vi2 < verts.Count)
                        faces.Add(new MeshFace(new[] { vi0, vi1, vi2 },
                                               new[] { ti0, ti1, ti2 },
                                               new[] { ni0, ni1, ni2 }));
                }
            }
            return (verts, uvs, norms, faces, bMin, bMax);
        }
 
        // Namespace-agnostic XML helpers
        private static XmlElement FindFirst(XmlNode root, string localName)
        {
            if (root == null) return null;
            foreach (XmlNode n in root.ChildNodes)
            {
                if (n is XmlElement el && el.LocalName == localName) return el;
                var found = FindFirst(n, localName);
                if (found != null) return found;
            }
            return null;
        }
 
        private static List<XmlElement> FindAll(XmlNode root, string localName)
        {
            var list = new List<XmlElement>();
            if (root == null) return list;
            foreach (XmlNode n in root.ChildNodes)
                if (n is XmlElement el && el.LocalName == localName) list.Add(el);
            return list;
        }
 
        private static void F(string s, out float v) =>
            float.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out v);
    }
 
    // --------------------------- FBX - Filmbox (ASCII Only) -----------------------
    // Supports ASCII FBX 7.x (Blender/Maya export). Binary FBX is unsupported.
    public static class FbxLoader
    {
        public static (List<Vector3> v, List<Vector2> uv, List<Vector3> n, List<MeshFace> f, Vector3 bMin, Vector3 bMax)
            Load(string path)
        {
            var verts = new List<Vector3>(); var uvs = new List<Vector2>();
            var norms = new List<Vector3>(); var faces = new List<MeshFace>();
            var bMin  = new Vector3( float.MaxValue,  float.MaxValue,  float.MaxValue);
            var bMax  = new Vector3(-float.MaxValue, -float.MaxValue, -float.MaxValue);
 
            // Binary FBX starts with "Kaydara FBX Binary  \0"
            byte[] header = new byte[23];
            using (var fs = File.OpenRead(path)) fs.Read(header, 0, 23);
            if (Encoding.ASCII.GetString(header, 0, 18) == "Kaydara FBX Binary")
                throw new Exception("Binary FBX is not supported.\nPlease re-export using 'ASCII FBX' from your 3D application.");
 
            var lines = File.ReadAllLines(path);
            float[] rawV = null, rawUV = null;
            int[]   polyIdx = null, uvIdx = null;
 
            for (int li = 0; li < lines.Length; li++)
            {
                var t = lines[li].Trim();
                // Match top-level array keywords
                if      (t.StartsWith("Vertices:"))          rawV    = ReadFbxFloatBlock(lines, li);
                else if (t.StartsWith("PolygonVertexIndex:")) polyIdx = ReadFbxIntBlock(lines, li);
                else if (t.StartsWith("UV:") && !t.StartsWith("UVIndex")) rawUV = ReadFbxFloatBlock(lines, li);
                else if (t.StartsWith("UVIndex:"))           uvIdx   = ReadFbxIntBlock(lines, li);
            }
 
            if (rawV != null)
                for (int i = 0; i + 2 < rawV.Length; i += 3)
                {
                    var vert = new Vector3(rawV[i], rawV[i + 1], rawV[i + 2]);
                    verts.Add(vert);
                    bMin = Vector3.ComponentMin(bMin, vert);
                    bMax = Vector3.ComponentMax(bMax, vert);
                }
 
            if (rawUV != null)
                for (int i = 0; i + 1 < rawUV.Length; i += 2)
                    uvs.Add(new Vector2(rawUV[i], rawUV[i + 1]));
 
            // Decode FBX polygon list (negative index = last vertex of polygon via ~value)
            if (polyIdx != null)
            {
                var poly = new List<int>();
                int uvCursor = 0;
                foreach (int idx in polyIdx)
                {
                    if (idx < 0)
                    {
                        poly.Add(~idx);
                        FbxTriangulatePoly(poly, verts, uvs, uvIdx, ref uvCursor, faces);
                        poly.Clear();
                    }
                    else poly.Add(idx);
                }
            }
 
            return (verts, uvs, norms, faces, bMin, bMax);
        }
 
        private static void FbxTriangulatePoly(List<int> poly, List<Vector3> verts,
            List<Vector2> uvs, int[] uvIdx, ref int uvCursor, List<MeshFace> faces)
        {
            if (poly.Count < 3) { uvCursor += poly.Count; return; }
            for (int i = 1; i < poly.Count - 1; i++)
            {
                int vi0 = poly[0], vi1 = poly[i], vi2 = poly[i + 1];
                if (vi0 >= verts.Count || vi1 >= verts.Count || vi2 >= verts.Count) continue;
                int ti0 = uvIdx != null && uvCursor     < uvIdx.Length ? Math.Max(0, uvIdx[uvCursor])     : 0;
                int ti1 = uvIdx != null && uvCursor + i < uvIdx.Length ? Math.Max(0, uvIdx[uvCursor + i]) : 0;
                int ti2 = uvIdx != null && uvCursor+i+1 < uvIdx.Length ? Math.Max(0, uvIdx[uvCursor+i+1]) : 0;
                ti0 = ti0 < uvs.Count ? ti0 : 0;
                ti1 = ti1 < uvs.Count ? ti1 : 0;
                ti2 = ti2 < uvs.Count ? ti2 : 0;
                faces.Add(new MeshFace(new[] { vi0, vi1, vi2 },
                                       new[] { ti0, ti1, ti2 },
                                       new[] { -1, -1, -1 }));
            }
            uvCursor += poly.Count;
        }
 
        private static float[] ReadFbxFloatBlock(string[] lines, int startLine)
        {
            var vals = new List<float>();
            for (int i = startLine; i < Math.Min(startLine + 500, lines.Length); i++)
            {
                var t = lines[i].Trim();
                if (i > startLine && t == "}") break;
                string data = t.StartsWith("a:") ? t.Substring(2) : (i == startLine && t.Contains(":") ? t.Substring(t.IndexOf(':') + 1) : t);
                foreach (var tok in data.Split(','))
                {
                    var s = tok.Trim();
                    if (s.Length > 0 && float.TryParse(s, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float fv)) vals.Add(fv);
                }
            }
            return vals.ToArray();
        }
 
        private static int[] ReadFbxIntBlock(string[] lines, int startLine)
        {
            var vals = new List<int>();
            for (int i = startLine; i < Math.Min(startLine + 500, lines.Length); i++)
            {
                var t = lines[i].Trim();
                if (i > startLine && t == "}") break;
                string data = t.StartsWith("a:") ? t.Substring(2) : (i == startLine && t.Contains(":") ? t.Substring(t.IndexOf(':') + 1) : t);
                foreach (var tok in data.Split(','))
                {
                    var s = tok.Trim();
                    if (s.Length > 0 && int.TryParse(s, out int iv)) vals.Add(iv);
                }
            }
            return vals.ToArray();
        }
    }
 
 
// =============================================================================
//  THE ENDING - PROGRAM ENTRY POINT
// =============================================================================
 
    static class Program
    {
        [System.STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Viewer3DForm());
        }
    }
 
}  // namespace Viewer3D
 
