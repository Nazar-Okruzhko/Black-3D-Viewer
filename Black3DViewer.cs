using System;
using System.Collections.Generic;
using System.Linq;
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
uniform int  shadingFlatSlot;   // active slot for mode 4 (flat raw texture)
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
    vec3 ambient  = 0.15 * lc * albedo;
    vec3 diffuse  = kD * d * lc * albedo * 0.7;
    vec3 specular = F * s * sstr * lc * 0.4;
    return ambient + (diffuse + specular) * att;
}
 
void main()
{
    if (shadingMode == 1) { FragColor = vec4(0.86, 0.86, 0.86, 1.0); return; }
    if (shadingMode == 3) {
        // Dark-blue = front-facing (normals pointing outward)
        // Red       = back-facing  (normals pointing inward)
        FragColor = gl_FrontFacing
            ? vec4(0.10, 0.15, 0.65, 1.0)
            : vec4(0.75, 0.08, 0.08, 1.0);
        return;
    }
    // Mode 4 = flat raw texture for the active slot (no lighting, no depth)
    if (shadingMode == 4) {
        vec3 raw = solidColor;
        if      (shadingFlatSlot == 0 && hasColorMap     != 0) raw = texture(colorMap,     TexCoord).rgb;
        else if (shadingFlatSlot == 1 && hasNormalMap    != 0) raw = texture(normalMap,    TexCoord).rgb;
        else if (shadingFlatSlot == 2 && hasSpecularMap  != 0) raw = vec3(texture(specularMap,  TexCoord).r);
        else if (shadingFlatSlot == 3 && hasRoughnessMap != 0) raw = vec3(texture(roughnessMap, TexCoord).r);
        else if (shadingFlatSlot == 4 && hasMetallicMap  != 0) raw = vec3(texture(metallicMap,  TexCoord).r);
        else if (shadingFlatSlot == 5 && hasOpacityMap   != 0) raw = vec3(texture(opacityMap,   TexCoord).r);
        FragColor = vec4(raw, 1.0);
        return;
    }
 
    // Mode 0 = Solid shading: clean Lambert diffuse + ambient, no texture/PBR complexity
    if (shadingMode == 0) {
        vec3  norm  = normalize(Normal);
        vec3  ld    = normalize(lightPos - FragPos);
        float diff  = max(dot(norm, ld), 0.0);
        // Soft fill from the opposite/below direction for visual depth
        vec3  fill_dir = normalize(vec3(-ld.x, abs(ld.y), -ld.z));
        float fill  = max(dot(norm, fill_dir), 0.0) * 0.18;
        float light = 0.38 + 0.60 * diff + fill;
        FragColor = vec4(solidColor * clamp(light, 0.0, 1.0), 1.0);
        return;
    }
 
 
    vec3 norm = normalize(Normal);
    if (hasNormalMap != 0)
    {
        vec3 nt = texture(normalMap, TexCoord).rgb * 2.0 - 1.0;
        norm = normalize(TBN * nt);
    }
 
    // albedo: color map when available, otherwise the solid grey
    vec3 albedo = solidColor;
    if (hasColorMap != 0) albedo = texture(colorMap, TexCoord).rgb;
 
    float rough = (hasRoughnessMap != 0) ? texture(roughnessMap, TexCoord).r : 0.5;
    float metal = (hasMetallicMap  != 0) ? texture(metallicMap,  TexCoord).r : 0.0;
    float sstr  = (hasSpecularMap  != 0) ? texture(specularMap,  TexCoord).r : 0.4;
 
    vec3 vd     = normalize(viewPos - FragPos);
    vec3 result = shade(lightPos,  vec3(1.0, 1.0, 1.0), norm, vd, albedo, rough, metal, sstr);
 
    float alpha = (hasOpacityMap != 0) ? texture(opacityMap, TexCoord).r
              : (hasColorMap    != 0) ? texture(colorMap,    TexCoord).a
              : 1.0;
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
        public const string OverlayVert = @"
#version 330 core
layout(location = 0) in vec3 aPos;
uniform mat4 model;
uniform mat4 view;
uniform mat4 projection;
void main() { gl_Position = projection * view * model * vec4(aPos, 1.0); }
";
        public const string OverlayFrag = @"
#version 330 core
out vec4 FragColor;
uniform vec4 addColor;
void main() { FragColor = addColor; }
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
        public void SetVec4(string n, Vector4 v)     => GL.Uniform4(Loc(n), v.X, v.Y, v.Z, v.W);
    }
 
// =============================================================================
//  SECTION 3 - SHARED MESH FACE TYPE
// =============================================================================
 
    public class MeshFace
    {
        public int[] VI;   // vertex indices (always length 3)
        public int[] TI;   // texcoord indices
        public int[] NI;   // normal indices  (-1 = none)
 
        public int GrpId;
        public MeshFace(int[] vi, int[] ti, int[] ni, int grpId=0)
        { VI = vi; TI = ti; NI = ni; GrpId = grpId; }
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
                    case ".csv":
                        // Ask user which columns hold X,Y,Z,U,V via popup
                        int cx=2,cy=3,cz=4,cu=5,cvv=6; bool hdr=true;
                        if (CsvFieldsSelector != null)
                        {
                            var csvAns = CsvFieldsSelector(path);
                            if (!csvAns.HasValue) return; // user cancelled
                            cx=csvAns.Value.colX; cy=csvAns.Value.colY; cz=csvAns.Value.colZ;
                            cu=csvAns.Value.colU; cvv=csvAns.Value.colV; hdr=csvAns.Value.header;
                        }
                        (v, uv, n, f, bMin, bMax) = CsvLoader.Load(path, cx, cy, cz, cu, cvv, hdr);
                        break;
                    case ".stl": (v, uv, n, f, bMin, bMax) = StlLoader.Load(path); break;
                    case ".rip": (v, uv, n, f, bMin, bMax) = NR1Loader.Load(path); break;
                    case ".nr":  (v, uv, n, f, bMin, bMax) = NR2Loader.Load(path); break;
                    case ".glb": (v, uv, n, f, bMin, bMax) = GlbLoader.Load(path); break;
                    case ".dae": (v, uv, n, f, bMin, bMax) = DaeLoader.Load(path); break;
                    case ".fbx":
                        // Detect binary vs ASCII by magic header
                        (v, uv, n, f, bMin, bMax) = IsFbxBinary(path)
                            ? FbxBinaryLoader.Load(path)
                            : FbxLoader.Load(path);
                        break;
                    case ".ply": (v, uv, n, f, bMin, bMax) = PlyLoader.Load(path); break;
                    case ".smd": (v, uv, n, f, bMin, bMax) = SmdLoader.Load(path); break;
                    case ".mdl": (v, uv, n, f, bMin, bMax) = MdlLoader.Load(path); break;
                    case ".3ds": (v, uv, n, f, bMin, bMax) = ThreeDsLoader.Load(path); break;
                    case ".max":
                        throw new Exception(
                            "Autodesk MAX (.max) is a proprietary binary format and cannot be opened without the 3ds Max SDK.\n" +
                            "Please export your scene as FBX, OBJ, or 3DS from within 3ds Max.");
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
 
        // Returns true when the file starts with the Kaydara FBX Binary magic string
        private static bool IsFbxBinary(string path)
        {
            try
            {
                using (var fs = File.OpenRead(path))
                {
                    var buf = new byte[18];
                    fs.Read(buf, 0, 18);
                    return System.Text.Encoding.ASCII.GetString(buf, 0, 18) == "Kaydara FBX Binary";
                }
            }
            catch { return false; }
        }
 
        public float   GetSize()   => Math.Max(Math.Max(BoundsMax.X - BoundsMin.X, BoundsMax.Y - BoundsMin.Y), BoundsMax.Z - BoundsMin.Z);
        public Vector3 GetCenter() => (BoundsMin + BoundsMax) * 0.5f;
        // -- Find geometrically disconnected parts (Union-Find) � for Shift mode --
        public List<LoosePart> FindLooseParts()
        {
            int n=Vertices.Count;
            if(n==0||Faces.Count==0)return new List<LoosePart>();
            int[] par=new int[n];for(int i=0;i<n;i++)par[i]=i;
            Func<int,int> Find=null;
            Find=(x)=>{while(par[x]!=x){par[x]=par[par[x]];x=par[x];}return x;};
            Action<int,int> Union=(a,b)=>{int ra=Find(a),rb=Find(b);if(ra!=rb)par[ra]=rb;};
            var pm=new Dictionary<long,int>(n);
            for(int i=0;i<n;i++)
            {
                var p=Vertices[i];
                long key=(long)(p.X*1000f)*1000000007L+(long)(p.Y*1000f)*1000003L+(long)(p.Z*1000f);
                int ex;if(pm.TryGetValue(key,out ex))Union(i,ex);else pm[key]=i;
            }
            foreach(var face in Faces)
                if(face.VI[0]<n&&face.VI[1]<n&&face.VI[2]<n)
                {Union(face.VI[0],face.VI[1]);Union(face.VI[1],face.VI[2]);}
            var groups=new Dictionary<int,LoosePart>();
            for(int fi=0;fi<Faces.Count;fi++)
            {
                int vi0=Faces[fi].VI[0];if(vi0>=n)continue;
                int root=Find(vi0);
                LoosePart lp;if(!groups.TryGetValue(root,out lp)){lp=new LoosePart();groups[root]=lp;}
                lp.FaceIndices.Add(fi);
            }
            return new List<LoosePart>(groups.Values);
        }
 
        // -- Find file-defined submeshes (OBJ g/o/usemtl groups) � for Ctrl mode --
        public List<LoosePart> FindSubmeshes()
        {
            var groups=new Dictionary<int,LoosePart>();
            for(int fi=0;fi<Faces.Count;fi++)
            {
                int gid=Faces[fi].GrpId;
                LoosePart lp;if(!groups.TryGetValue(gid,out lp)){lp=new LoosePart();groups[gid]=lp;}
                lp.FaceIndices.Add(fi);
            }
            // Fallback: if only one group (no g/o tags), split by face material index
            if(groups.Count<=1) return FindLooseParts();
            return new List<LoosePart>(groups.Values);
        }
 
        // Load a texture into GPU and return its ID without assigning to model slots
        public int LoadTexAndGetId(string path){return LoadTex(path);}
 
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
 
        // Prompt shown before loading .csv files; assigned by Viewer3DForm.
        // Returns column mapping or null when the user cancels.
        public static Func<string, (int colX,int colY,int colZ,int colU,int colV,bool header)?> CsvFieldsSelector = null;
 
        // -- Normal recalculation (smooth, always outward-facing) --------------
        public void RecalcNormals()
        {
            var center = GetCenter();
            var acc    = new Vector3[Vertices.Count];
            foreach (var face in Faces)
            {
                if (face.VI[0] >= Vertices.Count ||
                    face.VI[1] >= Vertices.Count ||
                    face.VI[2] >= Vertices.Count) continue;
 
                var v0 = Vertices[face.VI[0]];
                var v1 = Vertices[face.VI[1]];
                var v2 = Vertices[face.VI[2]];
                var e1 = v1 - v0;
                var e2 = v2 - v0;
                var fn = Vector3.Cross(e1, e2);
                if (fn.LengthSquared < 1e-14f) continue;
                fn.Normalize();
 
                // Ensure the normal points away from the model centre.
                // This corrects inverted winding (e.g. from axis-swap loaders).
                var faceCenter = (v0 + v1 + v2) * (1f / 3f);
                if (Vector3.Dot(fn, faceCenter - center) < 0f) fn = -fn;
 
                acc[face.VI[0]] += fn;
                acc[face.VI[1]] += fn;
                acc[face.VI[2]] += fn;
            }
            // After recalc, NI must equal VI (one normal per vertex)
            Normals.Clear();
            for (int i = 0; i < acc.Length; i++)
            {
                if (acc[i].LengthSquared < 1e-14f) acc[i] = Vector3.UnitY;
                else acc[i].Normalize();
                Normals.Add(acc[i]);
            }
            foreach (var face in Faces)
                for (int j = 0; j < 3; j++)
                    face.NI[j] = face.VI[j];
        }
 
        public void FlipNormals()
        {
            for (int i = 0; i < Normals.Count; i++)
                Normals[i] = -Normals[i];
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
        public void BuildBuffers()
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
 
        public int LoadTexture(string path, int slot)
        {
            int id = LoadTex(path); if (id < 0) return -1;
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
            return id;
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
        public void Render(Shader sh, int mode, int flatSlot = -1, Vector3? solidColor = null)
        {
            sh.Use();
            sh.SetInt("shadingMode", mode);
            sh.SetInt("shadingFlatSlot", flatSlot);
            sh.SetVec3("solidColor", solidColor ?? new Vector3(0.86f, 0.86f, 0.86f));
 
            bool doTex = (mode == 2 || mode == 4);
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
//  SECTION 4b � LOOSE PART / SUBMESH
// =============================================================================
    public class LoosePart
    {
        public List<int> FaceIndices = new List<int>();
        public int[]    TexIds   = new int[]    { -1,-1,-1,-1,-1,-1 };
        public string[] TexPaths = new string[6];
        public bool     HasTexOverride
        { get { foreach(var id in TexIds) if(id>=0) return true; return false; } }
        public int VAO, VBO, EBO; private int _cnt;
 
        public void BuildBuffers(GPUModel src)
        {
            if(VAO!=0){GL.DeleteVertexArray(VAO);GL.DeleteBuffer(VBO);GL.DeleteBuffer(EBO);}
            var vd=new List<float>(); var idx=new List<uint>();
            foreach(int fi in FaceIndices)
            {
                var face=src.Faces[fi];
                for(int j=0;j<3;j++)
                {
                    int vi=Math.Min(face.VI[j],src.Vertices.Count-1);
                    int ti=face.TI[j]; if(ti<0||ti>=src.TexCoords.Count) ti=-1;
                    int ni=face.NI[j]; if(ni<0||ni>=src.Normals.Count)   ni=-1;
                    var p=src.Vertices[vi];
                    vd.Add(p.X);vd.Add(p.Y);vd.Add(p.Z);
                    if(ni>=0){var n=src.Normals[ni];  vd.Add(n.X);vd.Add(n.Y);vd.Add(n.Z);}
                    else     {                         vd.Add(0f); vd.Add(1f); vd.Add(0f);}
                    if(ti>=0){var u=src.TexCoords[ti];vd.Add(u.X);vd.Add(u.Y);}
                    else     {                         vd.Add(0f); vd.Add(0f);}
                    vd.Add(1f);vd.Add(0f);vd.Add(0f);
                    idx.Add((uint)idx.Count);
                }
            }
            VAO=GL.GenVertexArray();VBO=GL.GenBuffer();EBO=GL.GenBuffer();
            GL.BindVertexArray(VAO);
            GL.BindBuffer(BufferTarget.ArrayBuffer,VBO);
            GL.BufferData(BufferTarget.ArrayBuffer,vd.Count*sizeof(float),vd.ToArray(),BufferUsageHint.StaticDraw);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer,EBO);
            GL.BufferData(BufferTarget.ElementArrayBuffer,idx.Count*sizeof(uint),idx.ToArray(),BufferUsageHint.StaticDraw);
            int st=11*sizeof(float);
            GL.VertexAttribPointer(0,3,VertexAttribPointerType.Float,false,st,0);               GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(1,3,VertexAttribPointerType.Float,false,st,3*sizeof(float)); GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(2,2,VertexAttribPointerType.Float,false,st,6*sizeof(float)); GL.EnableVertexAttribArray(2);
            GL.VertexAttribPointer(3,3,VertexAttribPointerType.Float,false,st,8*sizeof(float)); GL.EnableVertexAttribArray(3);
            GL.BindVertexArray(0);_cnt=idx.Count;
        }
        public void Render()
        { if(VAO==0)return; GL.BindVertexArray(VAO); GL.DrawElements(PrimitiveType.Triangles,_cnt,DrawElementsType.UnsignedInt,0); GL.BindVertexArray(0); }
        public void Cleanup()
        { if(VAO!=0){GL.DeleteVertexArray(VAO);GL.DeleteBuffer(VBO);GL.DeleteBuffer(EBO);VAO=VBO=EBO=0;} }
 
        // M�ller�Trumbore ray test
        public bool HitTest(Vector3 ro, Vector3 rd, GPUModel src, out float tMin)
        {
            tMin=float.MaxValue; bool hit=false;
            foreach(int fi in FaceIndices)
            {
                var face=src.Faces[fi];
                var v0=src.Vertices[Math.Min(face.VI[0],src.Vertices.Count-1)];
                var v1=src.Vertices[Math.Min(face.VI[1],src.Vertices.Count-1)];
                var v2=src.Vertices[Math.Min(face.VI[2],src.Vertices.Count-1)];
                var e1=v1-v0;var e2=v2-v0;
                var h=Vector3.Cross(rd,e2);float a=Vector3.Dot(e1,h);
                if(Math.Abs(a)<1e-7f)continue;
                float f=1f/a;var s=ro-v0;
                float u=f*Vector3.Dot(s,h);if(u<0f||u>1f)continue;
                var q=Vector3.Cross(s,e1);float vv=f*Vector3.Dot(rd,q);
                if(vv<0f||u+vv>1f)continue;
                float t=f*Vector3.Dot(e2,q);
                if(t>1e-4f&&t<tMin){tMin=t;hit=true;}
            }
            return hit;
        }
    }
 
// =============================================================================
//  SECTION 5 - FORM SETUP & UI CONTROLS
// =============================================================================
 
    // Eliminates flicker by compositing into an offscreen buffer before display.
    // Also suppresses OnPaintBackground so our checker/texture covers every pixel.
    sealed class DoubleBufferedPanel : Panel
    {
        public DoubleBufferedPanel()
        {
            SetStyle(ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
            UpdateStyles();
        }
        // Don't let WinForms erase the background ? our OnPaint covers 100% of pixels.
        protected override void OnPaintBackground(PaintEventArgs e) { }
    }
 
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
        private bool showUV, showGrid = true, showAxes, showNormals;
        // -- Loose-parts (Shift) + Submeshes (Ctrl) --------------------------------
        private List<LoosePart> _looseParts    = new List<LoosePart>(); // Shift: loose parts
        private List<LoosePart> _submeshes     = new List<LoosePart>(); // Ctrl:  file groups
        private HashSet<int>    _selectedParts  = new HashSet<int>();   // Shift-selected (for P/J)
        private HashSet<int>    _separatedParts = new HashSet<int>();   // after P key
        private int             _hoveredLoose   = -1;  // loose part under mouse (Shift mode)
        private int             _hoveredSub     = -1;  // submesh under mouse (Ctrl mode)
        private int             _activeSubmesh  = -1;  // Ctrl-clicked submesh ? receives textures
        private Shader          _overlayShader;
        private bool darkTheme = false;   // false = light (default), true = dark
        private Vector3 cachedCenter = Vector3.Zero;
        private float   cachedSize   = 5f;
 
        // -- GL resources --
        private Shader mainShader, wireShader;
        private int cubeVAO, cubeVBO;
 
        // -- Loaded model tracking --
        private string _loadedModelName = null;  // base name without extension
 
        // -- UV / texture preview cache --
        private Bitmap   uvCache;
        private Bitmap   _checkerBmp;               // checker background (cached per panel size)
        private Bitmap[] _slotBmps = new Bitmap[6]; // per-slot CPU bitmaps for preview panel
        private bool     uvDirty   = true;
        // -- UV-box animated overlays (0=hidden, 1=visible) --
        private float _uvT      = 0f;   // UV wireframe overlay alpha
        private float _uvTarget = 0f;
        private float _texAlpha = 1f;   // texture layer alpha (fades on slot change)
        private float _texTarget= 1f;
        // -- Texture hot-reload: poll LastWriteTime every 500 ms (works with all editors) --
        private readonly string[]   _hotPaths     = new string[6];
        private readonly DateTime[] _hotLastWrite = new DateTime[6];
        private System.Windows.Forms.Timer _hotReloadTimer;
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
 
            // Register CSV column-picker: shows a dialog before every .csv load
            GPUModel.CsvFieldsSelector = (csvPath) =>
            {
                using (var dlg = new CsvColumnsDialog(csvPath))
                {
                    dlg.ShowDialog(this);
                    if (!dlg.Confirmed) return null;
                    return (dlg.ColX, dlg.ColY, dlg.ColZ, dlg.ColU, dlg.ColV, dlg.HasHeader);
                }
            };
        }
 
        private void BuildUI()
        {
            Text        = "Black 3D Viewer";
            ClientSize  = new Size(1200, 800);
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
                ForeColor = Color.White, BackColor = Color.Transparent, Visible = false
            };
            glControl.Controls.Add(noTexLabel);
 
            // -- Right panel --
            tabControl = new TabControl { Dock = DockStyle.Right, Width = 290, Appearance = TabAppearance.FlatButtons };
            Controls.Add(tabControl);
 
            // --- Env & Light tab ---
            envLightTab = new TabPage("Env & Light") { BackColor = BG };
            tabControl.TabPages.Add(envLightTab);
 
            envLightTab.Controls.Add(new Label { Text = "Light Direction", Font = new Font("Segoe UI", 9, FontStyle.Bold), Location = new Point(70, 10), AutoSize = true, BackColor = BG });
 
            lightPanel = new DoubleBufferedPanel
            {
                Location = new Point(55, 35), Size = new Size(180, 180),
                BackColor = BG, BorderStyle = BorderStyle.None
            };
            lightPanel.Paint     += OnLightPaint;
            lightPanel.MouseDown += (s, e) => { dragLight = true;  UpdateLight(e.Location); };
            lightPanel.MouseUp   += (s, e) =>   dragLight = false;
            lightPanel.MouseMove += (s, e) => { if (dragLight) UpdateLight(e.Location); };
            envLightTab.Controls.Add(lightPanel);
 
            // --- Stats & Shading tab ---
            statsShadingTab = new TabPage("Stats & Shading") { BackColor = BG };
            tabControl.TabPages.Add(statsShadingTab);
 
            int y = 10;
            loadedLabel = new Label { Text = "Black 3D Viewer", Font = new Font("Segoe UI", 10, FontStyle.Bold),
                Location = new Point(10, y), Size = new Size(270, 26), BackColor = BG };
            statsShadingTab.Controls.Add(loadedLabel); y += 34;
 
            // UV preview panel (toggled by button; always pre-rendered)
            previewPanel = new DoubleBufferedPanel
            {
                Location = new Point(10, y), Size = new Size(270, 270),
                BackColor = Color.FromArgb(30,  30,  30),  BorderStyle = BorderStyle.FixedSingle
            };
            previewPanel.Paint  += OnUVPaint;
            previewPanel.Resize += (s, e) => { uvDirty = true; _checkerBmp?.Dispose(); _checkerBmp = null; previewPanel.Invalidate(); };
            statsShadingTab.Controls.Add(previewPanel); y += 280;
 
            // Shading
            solidBtn = SBtn(statsShadingTab, "Solid Shading", ref y); solidBtn.BackColor = PRESS;
            solidBtn.Click += (s, e) => { shadeMode = 0; RefreshShade(); };
 
            wireBtn = SBtn(statsShadingTab, "Wireframe View", ref y);
            wireBtn.Click  += (s, e) => { shadeMode = 1; RefreshShade(); };
 
            texBtn = SBtn(statsShadingTab, "Texture View", ref y);
            texBtn.Click   += (s, e) => { shadeMode = 2; RefreshTexBtns(); RefreshShade(); };
            y += 8;
 
            // Texture maps group
            var grp = new GroupBox { Text = "Texture Maps", Location = new Point(10, y), Size = new Size(270, 186), BackColor = BG, Font = new Font("Segoe UI", 10, FontStyle.Bold) };
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
 
            showTexBtn = SBtn(statsShadingTab, "Show Normals", ref y);
            showTexBtn.Click += (s, e) =>
            {
                showNormals = !showNormals;
                showTexBtn.BackColor = showNormals ? PRESS : IDLE;
                glControl.Invalidate();
            };
 
            uvBtn = SBtn(statsShadingTab, "Show UV Map", ref y);
            uvBtn.Click += (s, e) =>
            {
                showUV = !showUV;
                uvBtn.BackColor = showUV ? PRESS : IDLE;
                if (showUV)
                {
                    // UV turns ON  ? texture fades OUT, UV (dark bg + lines) fades IN
                    _uvTarget  = 1f;
                    _texTarget = 0f;
                    uvDirty    = true;
                }
                else
                {
                    // UV turns OFF ? UV fades OUT, texture fades back IN
                    _uvTarget  = 0f;
                    _texTarget = model != null && !string.IsNullOrEmpty(model.TexPaths[texSlot]) ? 1f : 0f;
                }
                _themeTimer.Start();
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
            var b = Btn(p, txt, 8, y, 254, 22); y += 26;
            b.Font = new Font("Segoe UI", 9f);  // override GroupBox bold inheritance
            return b;
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
                // Theme transition
                float diff = _themeTarget - _themeT;
                if (Math.Abs(diff) <= step) _themeT = _themeTarget;
                else                        _themeT += diff > 0 ? step : -step;
                // UV wireframe overlay transition
                float uvDiff = _uvTarget - _uvT;
                if (Math.Abs(uvDiff) <= step) _uvT = _uvTarget;
                else                          _uvT += uvDiff > 0 ? step : -step;
                // Texture layer alpha transition (slot switch fade)
                float texDiff = _texTarget - _texAlpha;
                if (Math.Abs(texDiff) <= step) _texAlpha = _texTarget;
                else                           _texAlpha += texDiff > 0 ? step : -step;
                // Stop when all three animations are idle
                if (Math.Abs(_themeTarget - _themeT)   < 0.001f &&
                    Math.Abs(_uvTarget    - _uvT)       < 0.001f &&
                    Math.Abs(_texTarget   - _texAlpha)  < 0.001f)
                    _themeTimer.Stop();
                previewPanel.Invalidate();
                glControl.Invalidate();
            };
 
            // Hot-reload: poll file LastWriteTime every 500 ms.
            // Works with ALL editors (Photoshop, Substance, GIMP) because it catches
            // atomic-rename saves that FileSystemWatcher.Changed never sees.
            _hotReloadTimer = new System.Windows.Forms.Timer { Interval = 500 };
            _hotReloadTimer.Tick += HotReloadTick;
            _hotReloadTimer.Start();
            _overlayShader = new Shader(ShaderSource.OverlayVert, ShaderSource.OverlayFrag);
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
 
            // Model stays at its original coordinate space (no centering translation)
            Matrix4 modelMat = Matrix4.Identity;
 
            // -- Wireframe mode --
            if (shadeMode == 1)
            {
                wireShader.Use();
                wireShader.SetMat4("projection", ref proj);
                wireShader.SetMat4("view",       ref view);
                wireShader.SetMat4("model",      ref modelMat);
 
                // --- Solid fill pass ---
                // Push the filled surface slightly back in depth so the line pass
                // always wins the depth test and every edge stays fully visible.
                GL.Enable(EnableCap.PolygonOffsetFill);
                GL.PolygonOffset(1.5f, 1.5f);
                wireShader.SetVec3("color", darkTheme
                    ? new Vector3(0.22f, 0.22f, 0.22f)
                    : new Vector3(0.82f, 0.82f, 0.82f));
                DrawMesh();
                GL.Disable(EnableCap.PolygonOffsetFill);
                GL.PolygonOffset(0f, 0f);
 
                // --- Line pass (edges) ---
                GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Line);
                GL.LineWidth(2.2f);
                wireShader.SetVec3("color", darkTheme
                    ? new Vector3(0.85f, 0.85f, 0.85f)
                    : new Vector3(0.04f, 0.04f, 0.04f));
                DrawMesh();
                GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
                GL.LineWidth(1f);
            }
            else
            {
                // -- Solid / Texture mode --
                mainShader.Use();
                mainShader.SetVec3("lightPos",  lp1);
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
                else
                {
                    int renderMode = showNormals ? 3 : shadeMode;
                    // For flat-slot mode (4) with no texture, use a colour that clearly
                    // contrasts the background so the model is always visible:
                    //   Light mode bg ? 0.867  ?  #2E2E2E (very dark)
                    //   Dark  mode bg ? 0.18   ?  #DDDDDD (very light)
                    // All other modes use the standard grey so Solid shading is unchanged.
                    Vector3 sc = (renderMode == 4)
                        ? (_themeT < 0.5f
                            ? new Vector3(0.180f, 0.180f, 0.180f)   // #2E2E2E
                            : new Vector3(0.867f, 0.867f, 0.867f))  // #DDDDDD
                        : new Vector3(0.86f, 0.86f, 0.86f);         // standard grey
                    model?.Render(mainShader, renderMode, texSlot, sc);
                }
            }
 
            // -- Grid & Axes ? always at world origin (0,0,0) --
            if (showGrid || showAxes)
            {
                wireShader.Use();
                wireShader.SetMat4("projection", ref proj);
                wireShader.SetMat4("view",       ref view);
                wireShader.SetMat4("model",      ref modelMat);
                GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
 
                // ---- Grid (thin GL_LINES, unchanged) ----
                if (showGrid)
                {
                    var lv = new List<float>();
                    float gs   = Math.Max(cachedSize, 2f) * 2f;
                    float step = gs / 10f;
                    float yFloor = 0f;
                    for (int gi = -10; gi <= 10; gi++)
                    {
                        lv.AddRange(new[] { gi*step, yFloor, -gs,   gi*step, yFloor,  gs });
                        lv.AddRange(new[] { -gs, yFloor, gi*step,    gs, yFloor, gi*step });
                    }
                    float[] lva = lv.ToArray();
                    int gVAO = GL.GenVertexArray(), gVBO = GL.GenBuffer();
                    GL.BindVertexArray(gVAO);
                    GL.BindBuffer(BufferTarget.ArrayBuffer, gVBO);
                    GL.BufferData(BufferTarget.ArrayBuffer, lva.Length * sizeof(float), lva, BufferUsageHint.StreamDraw);
                    GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3*sizeof(float), 0);
                    GL.EnableVertexAttribArray(0);
                    GL.LineWidth(1.8f);
                    var gc = darkTheme ? new Vector3(0.42f, 0.42f, 0.42f) : new Vector3(0.15f, 0.15f, 0.15f);
                    wireShader.SetVec3("color", gc);
                    GL.DrawArrays(PrimitiveType.Lines, 0, 21 * 4);
                    GL.BindVertexArray(0);
                    GL.DeleteVertexArray(gVAO); GL.DeleteBuffer(gVBO);
                }
 
                // ---- Axes (cross-section prism geometry, GL_TRIANGLES) ----
                // Each axis = 2 flat quads perpendicular to each other forming a "+" cross-section.
                // This is real 3D geometry so thickness is guaranteed on every driver.
                if (showAxes)
                {
                    float al = Math.Max(cachedSize * 0.12f, 0.5f);
                    float t  = Math.Max(al * 0.02f, 0.004f); // half-thickness ? slim but visible
 
                    var av = new List<float>();
                    // Adds a flat quad (p0,p1,p2,p3 in order) as 2 triangles
                    Action<float,float,float, float,float,float,
                           float,float,float, float,float,float> quad =
                        (x0,y0,z0, x1,y1,z1, x2,y2,z2, x3,y3,z3) =>
                    {
                        av.AddRange(new[]{ x0,y0,z0, x1,y1,z1, x2,y2,z2 });
                        av.AddRange(new[]{ x0,y0,z0, x2,y2,z2, x3,y3,z3 });
                    };
 
                    // X axis (0?al along X): quad in XY plane + quad in XZ plane
                    quad(0,-t,0,  0,t,0,  al,t,0,  al,-t,0);
                    quad(0,0,-t,  0,0,t,  al,0,t,  al,0,-t);
                    // Y axis (0?al along Y): quad in YX plane + quad in YZ plane
                    quad(-t,0,0,  t,0,0,  t,al,0,  -t,al,0);
                    quad(0,0,-t,  0,0,t,  0,al,t,  0,al,-t);
                    // Z axis (0?al along Z): quad in ZX plane + quad in ZY plane
                    quad(-t,0,0,  t,0,0,  t,0,al,  -t,0,al);
                    quad(0,-t,0,  0,t,0,  0,t,al,  0,-t,al);
 
                    float[] ava = av.ToArray();
                    int aVAO = GL.GenVertexArray(), aVBO = GL.GenBuffer();
                    GL.BindVertexArray(aVAO);
                    GL.BindBuffer(BufferTarget.ArrayBuffer, aVBO);
                    GL.BufferData(BufferTarget.ArrayBuffer, ava.Length * sizeof(float), ava, BufferUsageHint.StreamDraw);
                    GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3*sizeof(float), 0);
                    GL.EnableVertexAttribArray(0);
 
                    GL.Disable(EnableCap.CullFace); // render both faces of each quad
                    wireShader.SetVec3("color", new Vector3(0.9f,  0.15f, 0.15f)); GL.DrawArrays(PrimitiveType.Triangles, 0,  12); // X red
                    wireShader.SetVec3("color", new Vector3(0.15f, 0.85f, 0.15f)); GL.DrawArrays(PrimitiveType.Triangles, 12, 12); // Y green
                    wireShader.SetVec3("color", new Vector3(0.15f, 0.4f,  0.95f)); GL.DrawArrays(PrimitiveType.Triangles, 24, 12); // Z blue
 
                    GL.BindVertexArray(0);
                    GL.DeleteVertexArray(aVAO); GL.DeleteBuffer(aVBO);
                }
            }
 
            // -- A) Custom-texture parts � re-render over main pass ------------------
            if (model != null && !showCube && shadeMode != 1)
            {
                string[] _txU={"colorMap","normalMap","specularMap","roughnessMap","metallicMap","opacityMap"};
                string[] _hxU={"hasColorMap","hasNormalMap","hasSpecularMap","hasRoughnessMap","hasMetallicMap","hasOpacityMap"};
                bool _hasCTex=false;
                for(int _ci=0;_ci<_submeshes.Count;_ci++) if(_submeshes[_ci].HasTexOverride){_hasCTex=true;break;}
                if(_hasCTex)
                {
                    GL.Disable(EnableCap.Blend);
                    mainShader.Use();
                    mainShader.SetMat4("projection",ref proj);
                    mainShader.SetMat4("view",ref view);
                    mainShader.SetMat4("model",ref modelMat);
                    GL.DepthFunc(DepthFunction.Lequal);
                    for(int _ci=0;_ci<_submeshes.Count;_ci++)
                    {
                        var _clp=_submeshes[_ci]; if(!_clp.HasTexOverride)continue;
                        mainShader.SetInt("shadingMode",showNormals?3:shadeMode);
                        mainShader.SetInt("shadingFlatSlot",texSlot);
                        mainShader.SetVec3("solidColor",new Vector3(0.86f,0.86f,0.86f));
                        for(int _ts=0;_ts<6;_ts++)
                        {
                            GL.ActiveTexture(TextureUnit.Texture0+_ts);
                            int _tid=_clp.TexIds[_ts];
                            GL.BindTexture(TextureTarget.Texture2D,_tid>=0?_tid:0);
                            mainShader.SetInt(_txU[_ts],_ts);
                            mainShader.SetInt(_hxU[_ts],_tid>=0?1:0);
                        }
                        _clp.Render();
                    }
                    GL.DepthFunc(DepthFunction.Less);
                }
            }
 
            // -- B) Additive overlay � only while Shift or Ctrl is physically held ----
            // Z-fighting eliminated: overlay uses a shell matrix scaled 1.003� outward
            {
                bool _ovS = (Control.ModifierKeys & Keys.Shift)   != 0;
                bool _ovC = (Control.ModifierKeys & Keys.Control) != 0;
                bool _needOv = (_ovS && _hoveredLoose >= 0) || (_ovC && _hoveredSub >= 0)
                            || (_ovS && _selectedParts.Count > 0);
                if (_needOv && model != null && !showCube)
                {
                    // Scale the shell outward from the model's own centre so the
                    // overlay sits exactly on top of the geometry regardless of where
                    // the model lives in world space (avoids the upward-offset bug).
                    var _sc = cachedCenter;
                    var _shellMat = Matrix4.CreateTranslation(_sc)
                                 * Matrix4.CreateScale(1.003f)
                                 * Matrix4.CreateTranslation(-_sc);
                    _overlayShader.Use();
                    _overlayShader.SetMat4("projection", ref proj);
                    _overlayShader.SetMat4("view",       ref view);
                    _overlayShader.SetMat4("model",      ref _shellMat);
                    GL.Enable(EnableCap.Blend);
                    GL.BlendFunc(BlendingFactor.One, BlendingFactor.One);
                    GL.DepthMask(false);
 
                    // Ctrl mode: highlight hovered submesh R+12 G+12 B+12
                    if (_ovC && _hoveredSub >= 0 && _hoveredSub < _submeshes.Count)
                    {
                        _overlayShader.SetVec4("addColor", new Vector4(12f/255f,12f/255f,12f/255f,1f));
                        _submeshes[_hoveredSub].Render();
                    }
 
                    // Shift mode: highlight hovered loose part + selected parts
                    if (_ovS)
                    {
                        if (_hoveredLoose >= 0 && _hoveredLoose < _looseParts.Count)
                        {
                            _overlayShader.SetVec4("addColor", new Vector4(76f/255f,76f/255f,0f/255f,1f));
                            _looseParts[_hoveredLoose].Render();
                        }
                        foreach (int _si in _selectedParts)
                            if (_si < _looseParts.Count && _si != _hoveredLoose)
                            {
                                _overlayShader.SetVec4("addColor", new Vector4(38f/255f,38f/255f,0f/255f,1f));
                                _looseParts[_si].Render();
                            }
                    }
 
                    GL.DepthMask(true);
                    GL.Disable(EnableCap.Blend);
                }
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
            var g  = e.Graphics;
            int pw = previewPanel.Width, ph = previewPanel.Height;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
 
            // -- Always draw the UV-space background (dark + 4-quadrant grid) --
            g.Clear(Color.FromArgb(30, 30, 30));
            float uvSize  = Math.Min(pw, ph);
            float originX = (pw - uvSize) * 0.5f;
            float originY = (ph - uvSize) * 0.5f;
 
            // 8�8 grid (7 inner lines each axis)
            using (var gpMn = new Pen(Color.FromArgb(50, 50, 50), 1f))
            using (var gpMj = new Pen(Color.FromArgb(78, 78, 78), 1f))
            {
                for (int _gi = 1; _gi < 8; _gi++)
                {
                    float gx = originX + uvSize * _gi / 8f;
                    float gy = originY + uvSize * _gi / 8f;
                    var   gp = (_gi == 4) ? gpMj : gpMn;
                    g.DrawLine(gp, gx, originY, gx, originY + uvSize);
                    g.DrawLine(gp, originX, gy, originX + uvSize, gy);
                }
            }
            // UV-space boundary
            using (var bp = new Pen(Color.FromArgb(100, 100, 100), 1f))
                g.DrawRectangle(bp, originX, originY, uvSize - 1f, uvSize - 1f);
 
            if (model == null)
            {
                using (var f = new Font("Segoe UI", 9))
                    g.DrawString("No model loaded", f, Brushes.Gray, originX + 8, originY + 8);
                return;
            }
 
            // -- Layer 1: texture for selected slot (covers the grid when loaded) --
            // When a submesh is active (Ctrl+click), show that submesh's own texture.
            Bitmap slotBmp;
            Bitmap _partBmp = null;  // disposable temp created for submesh path
            if (_activeSubmesh >= 0 && _activeSubmesh < _submeshes.Count)
            {
                _partBmp = GetOrLoadSlotBitmapFromPart(_submeshes[_activeSubmesh], texSlot);
                slotBmp  = _partBmp ?? GetOrLoadSlotBitmap(texSlot);
            }
            else
            {
                slotBmp = GetOrLoadSlotBitmap(texSlot);
            }
            if (slotBmp != null)
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                g.DrawImage(slotBmp, 0, 0, pw, ph);
            }
            else if (_uvT <= 0.005f)
            {
                using (var f = new Font("Segoe UI", 9))
                    g.DrawString("No texture loaded", f, Brushes.Gray, originX + 8, originY + 8);
            }
            _partBmp?.Dispose(); // temp bitmap used only for this paint cycle
 
            // -- Layer 2: UV wireframe edges (only when Show UV Map is active) --
            if (_uvT > 0.005f && model.TexCoords.Count > 0)
            {
                if (uvDirty || uvCache == null) { RebuildUVCache(); uvDirty = false; }
                if (uvCache != null)
                {
                    using (var ia = new System.Drawing.Imaging.ImageAttributes())
                    {
                        var cm = new System.Drawing.Imaging.ColorMatrix { Matrix33 = _uvT };
                        ia.SetColorMatrix(cm);
                        g.DrawImage(uvCache, new Rectangle(0, 0, pw, ph),
                            0, 0, uvCache.Width, uvCache.Height, GraphicsUnit.Pixel, ia);
                    }
                }
            }
        }
        private void RebuildUVCache()
        {
            uvCache?.Dispose(); uvCache = null;
            int pw = previewPanel.Width, ph = previewPanel.Height;
            if (pw <= 2 || ph <= 2 || model == null) return;
 
            // Transparent background ? only the orange UV edge lines are stored here.
            // The dark background + 4-box grid are drawn directly in OnUVPaint every frame.
            var bmp = new Bitmap(pw, ph, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
 
                float uvSize  = Math.Min(pw, ph);
                float originX = (pw - uvSize) * 0.5f;
                float originY = (ph - uvSize) * 0.5f;
 
                // UV edges: when a submesh is active show only its faces; otherwise all.
                IEnumerable<MeshFace> _uvFaces;
                if (_activeSubmesh >= 0 && _activeSubmesh < _submeshes.Count)
                    _uvFaces = _submeshes[_activeSubmesh].FaceIndices
                                   .Where(fi => fi >= 0 && fi < model.Faces.Count)
                                   .Select(fi => model.Faces[fi]);
                else
                    _uvFaces = model.Faces;
 
                using (var pen = new Pen(Color.FromArgb(255, 165, 0), 1f))
                {
                    var drawn = new HashSet<long>();
                    foreach (var face in _uvFaces)
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
                                originX + uv1.X * uvSize,
                                originY + (1f - uv1.Y) * uvSize,
                                originX + uv2.X * uvSize,
                                originY + (1f - uv2.Y) * uvSize);
                        }
                }
            }
            uvCache = bmp;
        }
        private Bitmap GetCheckerBmp(int w, int h)
        {
            if (_checkerBmp != null && _checkerBmp.Width == w && _checkerBmp.Height == h)
                return _checkerBmp;
            _checkerBmp?.Dispose();
            if (w <= 0 || h <= 0) { _checkerBmp = null; return null; }
            int cell = 14;
            _checkerBmp = new Bitmap(w, h);
            using (var g = Graphics.FromImage(_checkerBmp))
            using (var b1 = new SolidBrush(Color.FromArgb(195, 195, 195)))
            using (var b2 = new SolidBrush(Color.FromArgb(122, 122, 122)))
            {
                for (int cx = 0; cx * cell < w; cx++)
                for (int cy = 0; cy * cell < h; cy++)
                {
                    var br = (cx + cy) % 2 == 0 ? (Brush)b1 : b2;
                    g.FillRectangle(br,
                        cx * cell, cy * cell,
                        Math.Min(cell, w - cx * cell),
                        Math.Min(cell, h - cy * cell));
                }
            }
            return _checkerBmp;
        }
 
        // -- Per-slot CPU bitmap for preview panel (lazy-loaded, cached until slot changes) ---
        private Bitmap GetOrLoadSlotBitmap(int slot)
        {
            if (model == null || slot < 0 || slot >= 6) return null;
            if (_slotBmps[slot] != null) return _slotBmps[slot];
            string path = model.TexPaths[slot];
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                Bitmap bmp;
                if      (ext == ".dds") bmp = model.LoadDDSPublic(path);
                else if (ext == ".tga") bmp = model.LoadTGAPublic(path);
                else                    bmp = new Bitmap(path);
                _slotBmps[slot] = bmp;
                return bmp;
            }
            catch { return null; }
        }
 
        // Load a preview bitmap directly from a LoosePart's own texture path.
        // Not cached in _slotBmps (which belongs to the whole-model textures);
        // result is thrown away after each paint and re-loaded lazily.
        private Bitmap GetOrLoadSlotBitmapFromPart(LoosePart part, int slot)
        {
            if (part == null || slot < 0 || slot >= 6) return null;
            // Prefer the part's own override; fall back to the model's path.
            string path = part.TexPaths[slot];
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                path = model?.TexPaths[slot];
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".dds") return model.LoadDDSPublic(path);
                if (ext == ".tga") return model.LoadTGAPublic(path);
                return new Bitmap(path);
            }
            catch { return null; }
        }
 
        private void ClearSlotBmps()
        {
            for (int i = 0; i < 6; i++) { _slotBmps[i]?.Dispose(); _slotBmps[i] = null; }
        }
 
// =============================================================================
//  SECTION 9 - CAMERA & INPUT
// =============================================================================
 
        // ProcessCmdKey fires before ANY focused control (including buttons) gets the key.
        // This is the only reliable way to intercept Enter without the Load button eating it.
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Return || keyData == Keys.Enter)
            {
                if (model != null) ExportOBJ();
                return true; // consumed ? do NOT forward to focused button
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
 
        private void OnKey(object sender, KeyEventArgs e)
        {
            if      (e.KeyCode == Keys.R) { ResetCam();    e.Handled = e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.F) { FocusModel();  e.Handled = e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.M) { ToggleTheme(); e.Handled = e.SuppressKeyPress = true; }
            // P � Separate Shift-selected loose parts
            else if (e.KeyCode == Keys.P && _selectedParts.Count > 0)
            {
                foreach(int i in _selectedParts) _separatedParts.Add(i);
                _selectedParts.Clear(); glControl.Invalidate();
                e.Handled = e.SuppressKeyPress = true;
            }
            // Ctrl+J � Rejoin selected separated loose parts
            else if (e.KeyCode == Keys.J && (e.Modifiers & Keys.Control) != 0)
            {
                foreach(int i in new List<int>(_selectedParts))
                {
                    _separatedParts.Remove(i);
                    if(i<_looseParts.Count)
                        for(int s=0;s<6;s++){_looseParts[i].TexIds[s]=-1;_looseParts[i].TexPaths[s]=null;}
                }
                _selectedParts.Clear(); glControl.Invalidate();
                e.Handled = e.SuppressKeyPress = true;
            }
            // Ctrl+N � Flip normals
            else if (e.KeyCode == Keys.N && (e.Modifiers & Keys.Control) != 0 && model != null)
            {
                model.FlipNormals();
                glControl.MakeCurrent(); model.BuildBuffers();
                foreach(var lp in _looseParts) lp.BuildBuffers(model);
                foreach(var sm in _submeshes)   sm.BuildBuffers(model);
                glControl.Invalidate(); e.Handled = e.SuppressKeyPress = true;
            }
            // N � Smooth normal recalculation
            else if (e.KeyCode == Keys.N && (e.Modifiers & Keys.Control) == 0 && model != null)
            {
                model.RecalcNormals();
                glControl.MakeCurrent(); model.BuildBuffers();
                foreach(var lp in _looseParts) lp.BuildBuffers(model);
                foreach(var sm in _submeshes)   sm.BuildBuffers(model);
                glControl.Invalidate(); e.Handled = e.SuppressKeyPress = true;
            }
 
        }
 
        private void FocusModel()
        {
            if (model == null) return;
            cachedCenter = model.GetCenter();
            cachedSize   = model.GetSize();
            lookAt       = cachedCenter;
            zoom         = -cachedSize * 1.8f;
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
                dlg.FileName = (string.IsNullOrEmpty(_loadedModelName) ? "model" : _loadedModelName) + ".obj";
                if (dlg.ShowDialog() != DialogResult.OK) return;
 
                string objPath  = dlg.FileName;
                string dir      = Path.GetDirectoryName(objPath);
                string baseName = Path.GetFileNameWithoutExtension(objPath);
                // Texture folder named exactly after the model (e.g. export_dir/soldier/)
                string texDir   = Path.Combine(dir, baseName);
 
                // --- Export textures as PNG ---
                // Slot codes: _c=colour _n=normal _s=specular _r=roughness _m=metallic _o=opacity
                string[] mtlKeys  = { "map_Kd","map_Bump","map_Ks","map_Pr","map_Pm","map_d" };
                string[] texSuffix = { "_c", "_n", "_s", "_r", "_m", "_o" };
                var exported = new Dictionary<int, string>();
                for (int s = 0; s < 6; s++)
                {
                    string src = model.TexPaths[s];
                    if (src == null || !File.Exists(src)) continue;
                    try
                    {
                        Directory.CreateDirectory(texDir);
                        string outName = baseName + texSuffix[s] + ".png";
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
                mtl.AppendLine("# Exported by HotDog 3D Viewer");
                mtl.AppendLine($"newmtl {baseName}_mat");
                mtl.AppendLine("Ka 1 1 1");  mtl.AppendLine("Kd 1 1 1");
                mtl.AppendLine("Ks 0 0 0");  mtl.AppendLine("d 1");
                foreach (var kv in exported)
                    mtl.AppendLine($"{mtlKeys[kv.Key]} {baseName}/{kv.Value}");
                File.WriteAllText(Path.Combine(dir, mtlName), mtl.ToString(), Encoding.UTF8);
 
                // --- Write OBJ ---
                var sb = new StringBuilder();
                sb.AppendLine("# Exported by HotDog 3D Viewer");
                sb.AppendLine($"mtllib {mtlName}");
                sb.AppendLine($"o {baseName}");
                sb.AppendLine("g default");
                sb.AppendLine($"usemtl {baseName}_mat");
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
                    msg += $"\n  {exported.Count} texture(s) \u2192 {baseName}/";
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
            glControl.Invalidate();
        }
 
        private void OnMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                bool ctrl  = (Control.ModifierKeys & Keys.Control) != 0;
                bool shift = (Control.ModifierKeys & Keys.Shift)   != 0;
 
                if (ctrl && model != null && !showCube && _hoveredSub >= 0)
                {
                    // Ctrl+Click: select/deselect submesh for texture assignment
                    _activeSubmesh = (_activeSubmesh == _hoveredSub) ? -1 : _hoveredSub;
                    uvDirty = true; previewPanel.Invalidate();
                    glControl.Invalidate(); return;
                }
                if (shift && model != null && !showCube && _hoveredLoose >= 0)
                {
                    // Shift+Click: toggle loose part in separation selection
                    if (_selectedParts.Contains(_hoveredLoose)) _selectedParts.Remove(_hoveredLoose);
                    else _selectedParts.Add(_hoveredLoose);
                    glControl.Invalidate(); return;
                }
 
                if ((DateTime.Now - lastClick).TotalMilliseconds < 300
                    && RayHitsModel(e.Location)) FocusModel();
                dragRot = true; lastMouse = e.Location; lastClick = DateTime.Now;
            }
            else if (e.Button == MouseButtons.Right)
            { dragPan = true; lastMouse = e.Location; }
            else if (e.Button == MouseButtons.Middle)
            { dragZoomMid = true; lastMouse = e.Location; }
        }
 
        // Ray-AABB test: returns true if the ray from mouse hits the loaded model bounds
        private bool RayHitsModel(Point mouse)
        {
            if (model == null) return false;
            float aspect = (float)glControl.Width / Math.Max(glControl.Height, 1);
            var proj = Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(45f), aspect, 0.01f, 5000f);
            float dist = Math.Abs(zoom);
            float rX = MathHelper.DegreesToRadians(rotX), rY = MathHelper.DegreesToRadians(rotY);
            var cam = lookAt + new Vector3(dist * (float)(Math.Sin(rY) * Math.Cos(rX)),
                                           dist * (float)Math.Sin(rX),
                                           dist * (float)(Math.Cos(rY) * Math.Cos(rX)));
            var view = Matrix4.LookAt(cam, lookAt, Vector3.UnitY);
            float nx = (2f * mouse.X) / glControl.Width - 1f;
            float ny = -(2f * mouse.Y) / glControl.Height + 1f;
            var eye4 = Vector4.Transform(new Vector4(nx, ny, -1f, 1f), Matrix4.Invert(proj));
            eye4 = new Vector4(eye4.X, eye4.Y, -1f, 0f);
            var w4 = Vector4.Transform(eye4, Matrix4.Invert(view));
            var rd = new Vector3(w4.X, w4.Y, w4.Z);
            if (rd.LengthSquared < 1e-10f) return false;
            rd.Normalize();
            // Slab AABB test against model bounds
            float[] ro = { cam.X, cam.Y, cam.Z };
            float[] rd3 = { rd.X, rd.Y, rd.Z };
            float[] bMin = { model.BoundsMin.X, model.BoundsMin.Y, model.BoundsMin.Z };
            float[] bMax = { model.BoundsMax.X, model.BoundsMax.Y, model.BoundsMax.Z };
            float tMin = float.NegativeInfinity, tMax = float.PositiveInfinity;
            for (int a = 0; a < 3; a++)
            {
                if (Math.Abs(rd3[a]) < 1e-8f)
                { if (ro[a] < bMin[a] || ro[a] > bMax[a]) return false; }
                else
                {
                    float t1 = (bMin[a] - ro[a]) / rd3[a];
                    float t2 = (bMax[a] - ro[a]) / rd3[a];
                    if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
                    tMin = Math.Max(tMin, t1);
                    tMax = Math.Min(tMax, t2);
                    if (tMin > tMax) return false;
                }
            }
            return tMax > 0f;
        }
 
        // Generic ray-pick into any LoosePart list; returns index or -1
        private int PickFrom(List<LoosePart> parts, Point mouse)
        {
            float aspect=(float)glControl.Width/Math.Max(glControl.Height,1);
            var proj=Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(45f),aspect,0.01f,5000f);
            float dist=Math.Abs(zoom);
            float rX=MathHelper.DegreesToRadians(rotX),rY=MathHelper.DegreesToRadians(rotY);
            var cam=lookAt+new Vector3(dist*(float)(Math.Sin(rY)*Math.Cos(rX)),
                                       dist*(float) Math.Sin(rX),
                                       dist*(float)(Math.Cos(rY)*Math.Cos(rX)));
            var view=Matrix4.LookAt(cam,lookAt,Vector3.UnitY);
            float nx=(2f*mouse.X)/glControl.Width-1f, ny=-(2f*mouse.Y)/glControl.Height+1f;
            var eye4=Vector4.Transform(new Vector4(nx,ny,-1f,1f),Matrix4.Invert(proj));
            eye4=new Vector4(eye4.X,eye4.Y,-1f,0f);
            var w4=Vector4.Transform(eye4,Matrix4.Invert(view));
            var rd=new Vector3(w4.X,w4.Y,w4.Z);
            if(rd.LengthSquared<1e-10f)return -1; rd.Normalize();
            int best=-1; float bestT=float.MaxValue;
            for(int i=0;i<parts.Count;i++)
            { float t; if(parts[i].HitTest(cam,rd,model,out t)&&t<bestT){bestT=t;best=i;} }
            return best;
        }
 
        private void OnMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)                                        dragRot = false;
            else if (e.Button == MouseButtons.Right)  dragPan     = false;
            else if (e.Button == MouseButtons.Middle) dragZoomMid = false;
        }
 
        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            bool _mmC=(Control.ModifierKeys & Keys.Control)!=0;
            bool _mmS=(Control.ModifierKeys & Keys.Shift)  !=0;
            if (!dragRot && !dragPan && !dragZoomMid && model != null && !showCube)
            {
                if (_mmC && _submeshes.Count > 0)
                {
                    int np=PickFrom(_submeshes,e.Location);
                    if(np!=_hoveredSub){_hoveredSub=np;glControl.Invalidate();}
                }
                else if (!_mmC && _hoveredSub != -1)
                { _hoveredSub=-1; glControl.Invalidate(); }
 
                if (_mmS && _looseParts.Count > 0)
                {
                    int np=PickFrom(_looseParts,e.Location);
                    if(np!=_hoveredLoose){_hoveredLoose=np;glControl.Invalidate();}
                }
                else if (!_mmS && _hoveredLoose != -1)
                { _hoveredLoose=-1; glControl.Invalidate(); }
            }
 
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
            var g  = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            // Match the surrounding tab background so the panel has no visible box
            g.Clear(BG);
            int cx = lightPanel.Width / 2, cy = lightPanel.Height / 2, r = 70;
            // Circle outline only (no fill ? much faster)
            using (var pen = new Pen(Color.FromArgb(190, 190, 190), 1.5f))
                g.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);
            // Gold ball at current light angle
            int lx = (int)(cx + Math.Cos(lightAngle) * (r - 12));
            int ly = (int)(cy - Math.Sin(lightAngle) * (r - 12));
            g.FillEllipse(Brushes.Gold, lx - 10, ly - 10, 20, 20);
            using (var pen = new Pen(Color.DarkGoldenrod, 1.5f))
                g.DrawEllipse(pen, lx - 10, ly - 10, 20, 20);
        }
 
        private void UpdateLight(Point p)
        {
            int cx = lightPanel.Width / 2, cy = lightPanel.Height / 2;
            lightAngle = (float)Math.Atan2(cy - p.Y, p.X - cx);
            lightPanel.Invalidate();
            lightPanel.Update();     // flush immediately so ball never lags
            glControl.Invalidate();  // GL update follows asynchronously
        }
 
// =============================================================================
//  SECTION 10 - FILE LOADING & DRAG-DROP
// =============================================================================
 
        private static readonly HashSet<string> MODEL_EXT = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".obj", ".csv", ".stl", ".rip", ".nr", ".glb", ".dae", ".fbx",
              ".ply", ".smd", ".mdl", ".3ds", ".max" };
 
        private static readonly HashSet<string> TEX_EXT = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".png", ".jpg", ".jpeg", ".bmp", ".dds", ".tga" };
 
        private void OpenDialog()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title  = "Load 3D Model";
                dlg.Filter =
                    "All Supported|*.obj;*.csv;*.stl;*.rip;*.nr;*.glb;*.dae;*.fbx;*.ply;*.smd;*.mdl;*.3ds|" +
                    "Wavefront OBJ (*.obj)|*.obj|" +
                    "Stanford PLY (*.ply)|*.ply|" +
                    "FBX ? ASCII & Binary (*.fbx)|*.fbx|" +
                    "Collada (*.dae)|*.dae|" +
                    "glTF Binary (*.glb)|*.glb|" +
                    "STL (*.stl)|*.stl|" +
                    "Valve SMD (*.smd)|*.smd|" +
                    "Valve GoldSrc MDL (*.mdl)|*.mdl|" +
                    "3D Studio (*.3ds)|*.3ds|" +
                    "CSV Geometry (*.csv)|*.csv|" +
                    "NinjaRipper v1 (*.rip)|*.rip|" +
                    "NinjaRipper v2 (*.nr)|*.nr|" +
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
            shadeMode    = 0;   // start in Solid Shading by default
            texSlot      = 0;
            cachedCenter = model.GetCenter();
            cachedSize   = model.GetSize();
            lookAt       = cachedCenter;
            zoom         = -cachedSize * 1.8f;
            rotX = 0f; rotY = 0f;
            uvDirty = true;
            ClearSlotBmps();   // reload previews on next paint
 
            _loadedModelName = Path.GetFileNameWithoutExtension(path);
            loadedLabel.Text = Path.GetFileName(path);
            Text = "Black 3D Viewer  —  " + Path.GetFileName(path);
            RefreshShade();
            RefreshTexBtns();
            RefreshTexBtnEnabled();
            UpdateStatLabels();
 
            // Set up file-change watchers for every loaded texture slot
            for (int ws = 0; ws < 6; ws++)
                TrackTexture(ws, model.TexPaths[ws]);
 
            glControl.MakeCurrent();
            _selectedParts.Clear(); _separatedParts.Clear();
            _hoveredLoose=-1; _hoveredSub=-1; _activeSubmesh=-1;
            foreach(var lp in _looseParts) lp.Cleanup();
            foreach(var sm in _submeshes)   sm.Cleanup();
            _looseParts = model.FindLooseParts();
            _submeshes  = model.FindSubmeshes();
            foreach(var lp in _looseParts) lp.BuildBuffers(model);
            foreach(var sm in _submeshes)   sm.BuildBuffers(model);
 
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
                // Always load a dropped texture into the Color slot and show it immediately
                glControl.MakeCurrent();
                if (_activeSubmesh >= 0 && _activeSubmesh < _submeshes.Count)
                {
                    // Assign to Ctrl-selected submesh only
                    var _alp=_submeshes[_activeSubmesh];
                    if(_alp.TexIds[0]>=0) GL.DeleteTexture(_alp.TexIds[0]);
                    _alp.TexIds[0]=model.LoadTexAndGetId(f);
                    _alp.TexPaths[0]=f;
                }
                else
                {
                    model.LoadTexture(f, 0);
                    TrackTexture(0, f);
                    _slotBmps[0]?.Dispose(); _slotBmps[0] = null;
                    SetSlot(0);
                }   // switches to flat Color-slot view instantly
                previewPanel.Invalidate();
                glControl.Invalidate();
            }
        }
 
// =============================================================================
//  SECTION 11 - UI STATE HELPERS
// =============================================================================
 
        private void SetSlot(int s)
        {
            texSlot = s;
            // Switch to flat raw-texture mode so the 3D view shows only this map, unlit
            shadeMode = 4;
            RefreshTexBtns();
            RefreshShade();      // sync shading-button highlights (texBtn deselected, etc.)
            UpdateNoTex();
            uvDirty = true;
            // Instant ? no fade animation
            _texAlpha  = (model != null && !string.IsNullOrEmpty(model.TexPaths[s])) ? 1f : 0f;
            _texTarget = _texAlpha;
            previewPanel.Invalidate();
            glControl.Invalidate();
        }
 
        private void RefreshShade()
        {
            solidBtn.BackColor   = shadeMode == 0 ? PRESS : IDLE;
            wireBtn.BackColor    = shadeMode == 1 ? PRESS : IDLE;
            // Texture View button is highlighted for both PBR (2) and flat-slot (4) modes
            texBtn.BackColor     = (shadeMode == 2 || shadeMode == 4) ? PRESS : IDLE;
            // Clicking any main shading button cancels the normals overlay
            if (shadeMode != 3) { showNormals = false; showTexBtn.BackColor = IDLE; }
            // If not in flat-slot mode, clear the slot button highlights
            if (shadeMode != 4) RefreshTexBtns();
            RefreshTexBtnEnabled();
            UpdateNoTex();
            previewPanel.Invalidate();
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
            // Buttons are always enabled ? never gray them out
            colBtn.Enabled = nrmBtn.Enabled = specBtn.Enabled =
            roughBtn.Enabled = metBtn.Enabled = opqBtn.Enabled = true;
        }
 
        private void UpdateNoTex()
        {
            if (model == null) { noTexLabel.Visible = false; return; }
            noTexLabel.Visible = (shadeMode == 2 || shadeMode == 4) && !model.HasTex(texSlot);
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
            _hotReloadTimer?.Stop(); _hotReloadTimer?.Dispose();
            _themeTimer?.Stop(); _themeTimer?.Dispose();
            model?.Cleanup();
            uvCache?.Dispose();
            _checkerBmp?.Dispose(); ClearSlotBmps();
            foreach(var lp in _looseParts) lp.Cleanup();
            foreach(var sm in _submeshes) sm.Cleanup();
                        if (cubeVAO != 0) GL.DeleteVertexArray(cubeVAO);
            if (cubeVBO != 0) GL.DeleteBuffer(cubeVBO);
        }
 
// =============================================================================
//  TEXTURE HOT-RELOAD  (polls LastWriteTime every 500 ms ? works with all editors)
// =============================================================================
 
        // Register a path to be polled; call whenever a texture slot is loaded/changed.
        private void TrackTexture(int slot, string path)
        {
            _hotPaths[slot]     = path;
            _hotLastWrite[slot] = string.IsNullOrEmpty(path) || !File.Exists(path)
                                  ? DateTime.MinValue
                                  : File.GetLastWriteTimeUtc(path);
        }
 
        // Runs on the UI thread via the WinForms timer ? safe to touch GL here.
        private void HotReloadTick(object sender, EventArgs e)
        {
            if (model == null) return;
            for (int s = 0; s < 6; s++)
            {
                string path = _hotPaths[s];
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
 
                DateTime wt;
                try { wt = File.GetLastWriteTimeUtc(path); } catch { continue; }
 
                if (wt <= _hotLastWrite[s]) continue;   // not changed
                _hotLastWrite[s] = wt;                   // update stamp first
 
                // Reload texture on GPU
                glControl.MakeCurrent();
                int oldId = model.GetTexId(s);
                if (oldId >= 0) GL.DeleteTexture(oldId);
                model.LoadTexture(path, s);
 
                _slotBmps[s]?.Dispose(); _slotBmps[s] = null;  // force preview refresh for this slot
 
                RefreshTexBtnEnabled();
                UpdateNoTex();
                previewPanel.Invalidate();
                glControl.Invalidate();
            }
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
            int _grpId = 0;
            var bMin   = new Vector3( float.MaxValue,  float.MaxValue,  float.MaxValue);
            var bMax   = new Vector3(-float.MaxValue, -float.MaxValue, -float.MaxValue);
 
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var p = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 2) continue;
 
                if ((p[0]=="g"||p[0]=="o"||p[0]=="usemtl") && p.Length>=2)
                    _grpId++;
                else if (p[0] == "v" && p.Length >= 4)
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
                            new[] { ni[0], ni[i], ni[i + 1] }, _grpId));
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
        // colX/Y/Z = 0-based column indices for position; colU/V for texture coords.
        // Defaults match the common NinjaRipper/export layout: X=2 Y=3 Z=4 U=5 V=6.
        public static (List<Vector3> v, List<Vector2> uv, List<Vector3> n, List<MeshFace> f, Vector3 bMin, Vector3 bMax)
            Load(string path, int colX=2, int colY=3, int colZ=4, int colU=5, int colV=6, bool hasHeader=true)
        {
            var verts = new List<Vector3>(); var uvs = new List<Vector2>();
            var norms = new List<Vector3>(); var faces = new List<MeshFace>();
            var bMin  = new Vector3( float.MaxValue,  float.MaxValue,  float.MaxValue);
            var bMax  = new Vector3(-float.MaxValue, -float.MaxValue, -float.MaxValue);
 
            var lines = File.ReadAllLines(path);
            int startLine = hasHeader ? 1 : 0;
            int need = Math.Max(Math.Max(colX,colY), Math.Max(colZ,Math.Max(colU,colV))) + 1;
            int rowInFace = 0;
 
            for (int li = startLine; li < lines.Length; li++)
            {
                var p = lines[li].Split(',');
                if (p.Length < need) continue;
 
                float x  = F(p[colX]);
                float y  = F(p[colY]);
                float z  = -F(p[colZ]);             // mirror Z axis
                float u  = F(p[colU]);
                float vv = 1f - F(p[colV]);         // flip V
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
 
        // Peek at the first data row and return column header names
        public static string[] ReadHeaders(string path)
        {
            try
            {
                using (var sr = new StreamReader(path))
                {
                    string line = sr.ReadLine();
                    return line?.Split(',') ?? new string[0];
                }
            }
            catch { return new string[0]; }
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
 
 
 
    // --------------------------------------------------------------------------
    // PLY  Stanford Polygon Format  (ASCII + binary little/big endian)
    // --------------------------------------------------------------------------
    public static class PlyLoader
    {
        public static (List<Vector3> v, List<Vector2> uv, List<Vector3> n, List<MeshFace> f, Vector3 bMin, Vector3 bMax)
            Load(string path)
        {
            var verts = new List<Vector3>(); var uvs = new List<Vector2>();
            var norms = new List<Vector3>(); var faces = new List<MeshFace>();
            var bMin  = new Vector3( float.MaxValue,  float.MaxValue,  float.MaxValue);
            var bMax  = new Vector3(-float.MaxValue, -float.MaxValue, -float.MaxValue);
 
            // -- Parse header -------------------------------------------------
            string fmt = "ascii";
            int nVerts = 0, nFaces = 0;
            var vProps = new List<(string type, string name)>();
            var fProps = new List<(string type, string name)>();
            bool inVert = false, inFace = false;
            long dataStart = 0;
 
            using (var fs = File.OpenRead(path))
            {
                var hdr = new List<string>();
                var tmp = new List<byte>();
                int b;
                while ((b = fs.ReadByte()) != -1)
                {
                    if (b == '\n')
                    {
                        string ln = System.Text.Encoding.ASCII.GetString(tmp.ToArray()).Trim();
                        hdr.Add(ln); tmp.Clear();
                        if (ln == "end_header") { dataStart = fs.Position; break; }
                    }
                    else if (b != '\r') tmp.Add((byte)b);
                }
 
                foreach (var ln in hdr)
                {
                    var p = ln.Split(new[]{' '}, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length == 0) continue;
                    if (p[0] == "format")     { fmt = p.Length > 1 ? p[1] : "ascii"; }
                    else if (p[0] == "element")
                    {
                        inVert = p.Length > 1 && p[1] == "vertex"; inFace = p.Length > 1 && p[1] == "face";
                        if (inVert && p.Length > 2) int.TryParse(p[2], out nVerts);
                        if (inFace && p.Length > 2) int.TryParse(p[2], out nFaces);
                    }
                    else if (p[0] == "property")
                    {
                        if (inVert && p.Length >= 3) vProps.Add((p[1], p[p.Length - 1].ToLower()));
                        else if (inFace && p.Length >= 3) fProps.Add((p[1] == "list" ? "list" : p[1], p.Length > 3 ? p[3] : p[2]));
                    }
                }
            }
 
            // Map vertex property names ? indices
            int xi=-1,yi=-1,zi=-1,nxi=-1,nyi=-1,nzi=-1,si=-1,ti=-1;
            for (int i = 0; i < vProps.Count; i++)
            {
                switch (vProps[i].name) {
                    case "x":  xi=i; break; case "y": yi=i; break; case "z": zi=i; break;
                    case "nx": nxi=i;break; case "ny":nyi=i;break; case "nz":nzi=i;break;
                    case "s": case "u": si=i; break;
                    case "t": case "v": ti=i; break;
                }
            }
 
            bool ascii = fmt == "ascii";
            bool bigEndian = fmt == "binary_big_endian";
 
            using (var fs = File.OpenRead(path))
            {
                fs.Seek(dataStart, SeekOrigin.Begin);
                using (var br = new BinaryReader(fs))
                {
                    if (ascii)
                    {
                        // -- ASCII read ----------------------------------------
                        using (var sr = new StreamReader(fs, System.Text.Encoding.ASCII, false, 4096, true))
                        {
                            for (int vi = 0; vi < nVerts; vi++)
                            {
                                var tok = (sr.ReadLine() ?? "").Trim().Split(new[]{' ','\t'}, StringSplitOptions.RemoveEmptyEntries);
                                float[] vals = new float[tok.Length];
                                for (int k = 0; k < tok.Length; k++) FloatTryParse(tok[k], out vals[k]);
                                var vert = new Vector3(xi>=0?vals[xi]:0, yi>=0?vals[yi]:0, zi>=0?vals[zi]:0);
                                verts.Add(vert); bMin = Vector3.ComponentMin(bMin, vert); bMax = Vector3.ComponentMax(bMax, vert);
                                if (nxi>=0 && nyi>=0 && nzi>=0) norms.Add(new Vector3(vals[nxi],vals[nyi],vals[nzi]));
                                uvs.Add(si>=0&&ti>=0 ? new Vector2(vals[si],vals[ti]) : Vector2.Zero);
                            }
                            for (int fi = 0; fi < nFaces; fi++)
                            {
                                var tok = (sr.ReadLine() ?? "").Trim().Split(new[]{' ','\t'}, StringSplitOptions.RemoveEmptyEntries);
                                if (tok.Length < 1) continue;
                                int cnt; int.TryParse(tok[0], out cnt);
                                int[] idx = new int[cnt];
                                for (int k = 0; k < cnt && k+1 < tok.Length; k++) int.TryParse(tok[k+1], out idx[k]);
                                for (int k = 1; k < cnt-1; k++)
                                    faces.Add(new MeshFace(new[]{idx[0],idx[k],idx[k+1]},
                                                           new[]{idx[0],idx[k],idx[k+1]},
                                                           new[]{norms.Count>0?idx[0]:-1, norms.Count>0?idx[k]:-1, norms.Count>0?idx[k+1]:-1}));
                            }
                        }
                    }
                    else
                    {
                        // -- Binary read ---------------------------------------
                        for (int vi = 0; vi < nVerts; vi++)
                        {
                            float[] vals = new float[vProps.Count];
                            for (int k = 0; k < vProps.Count; k++)
                                vals[k] = ReadPlyFloat(br, vProps[k].type, bigEndian);
                            var vert = new Vector3(xi>=0?vals[xi]:0, yi>=0?vals[yi]:0, zi>=0?vals[zi]:0);
                            verts.Add(vert); bMin = Vector3.ComponentMin(bMin, vert); bMax = Vector3.ComponentMax(bMax, vert);
                            if (nxi>=0&&nyi>=0&&nzi>=0) norms.Add(new Vector3(vals[nxi],vals[nyi],vals[nzi]));
                            uvs.Add(si>=0&&ti>=0 ? new Vector2(vals[si],vals[ti]) : Vector2.Zero);
                        }
                        for (int fi = 0; fi < nFaces; fi++)
                        {
                            // First property is list: read count then indices
                            int cnt = (int)ReadPlyUInt(br, fProps.Count>0&&fProps[0].type=="list" ? "uchar" : "uchar", bigEndian);
                            int[] idx = new int[cnt];
                            for (int k = 0; k < cnt; k++) idx[k] = (int)ReadPlyUInt(br, "int", bigEndian);
                            for (int k = 1; k < cnt-1; k++)
                                faces.Add(new MeshFace(new[]{idx[0],idx[k],idx[k+1]},
                                                       new[]{idx[0],idx[k],idx[k+1]},
                                                       new[]{norms.Count>0?idx[0]:-1, norms.Count>0?idx[k]:-1, norms.Count>0?idx[k+1]:-1}));
                        }
                    }
                }
            }
            return (verts, uvs, norms, faces, bMin, bMax);
        }
 
        static float ReadPlyFloat(BinaryReader br, string t, bool big)
        {
            switch (t) {
                case "float": case "float32": { var b=br.ReadBytes(4); if(big)Array.Reverse(b); return BitConverter.ToSingle(b,0); }
                case "double": case "float64": { var b=br.ReadBytes(8); if(big)Array.Reverse(b); return (float)BitConverter.ToDouble(b,0); }
                default: return (float)ReadPlyUInt(br, t, big);
            }
        }
        static uint ReadPlyUInt(BinaryReader br, string t, bool big)
        {
            switch (t) {
                case "uchar": case "uint8":  return br.ReadByte();
                case "char":  case "int8":   return (uint)br.ReadSByte();
                case "ushort":case "uint16": { var b=br.ReadBytes(2); if(big)Array.Reverse(b); return BitConverter.ToUInt16(b,0); }
                case "short": case "int16":  { var b=br.ReadBytes(2); if(big)Array.Reverse(b); return (uint)BitConverter.ToInt16(b,0); }
                case "int":   case "int32":  { var b=br.ReadBytes(4); if(big)Array.Reverse(b); return (uint)BitConverter.ToInt32(b,0); }
                case "uint":  case "uint32": { var b=br.ReadBytes(4); if(big)Array.Reverse(b); return BitConverter.ToUInt32(b,0); }
                default: return 0;
            }
        }
        static void FloatTryParse(string s, out float v) =>
            float.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out v);
    }
 
    // --------------------------------------------------------------------------
    // SMD  Valve Source Model Data  (ASCII reference-mesh format)
    // --------------------------------------------------------------------------
    public static class SmdLoader
    {
        public static (List<Vector3> v, List<Vector2> uv, List<Vector3> n, List<MeshFace> f, Vector3 bMin, Vector3 bMax)
            Load(string path)
        {
            var verts = new List<Vector3>(); var uvs = new List<Vector2>();
            var norms = new List<Vector3>(); var faces = new List<MeshFace>();
            var bMin  = new Vector3( float.MaxValue,  float.MaxValue,  float.MaxValue);
            var bMax  = new Vector3(-float.MaxValue, -float.MaxValue, -float.MaxValue);
 
            var lines = File.ReadAllLines(path);
            bool inTri = false;
            int triLine = 0;   // 0=material, 1/2/3=vertices
            int[] vi3 = new int[3];
 
            for (int li = 0; li < lines.Length; li++)
            {
                var raw = lines[li].Trim();
                if (raw.Length == 0 || raw.StartsWith("//")) continue;
 
                if (raw == "triangles") { inTri = true; triLine = 0; continue; }
                if (raw == "end")       { inTri = false; triLine = 0; continue; }
                if (!inTri) continue;
 
                if (triLine == 0) { triLine = 1; continue; } // material line, skip
 
                var tok = raw.Split(new[]{' ','\t'}, StringSplitOptions.RemoveEmptyEntries);
                // format: bone  x y z  nx ny nz  u v  [links...]
                if (tok.Length < 9) { triLine = 1; continue; }
 
                float x  = F(tok[1]), y  = F(tok[2]),  z  = F(tok[3]);
                float nx = F(tok[4]), ny = F(tok[5]),  nz = F(tok[6]);
                float u  = F(tok[7]), tv = F(tok[8]);
 
                var vert = new Vector3(x, y, z);
                int idx = verts.Count;
                verts.Add(vert); uvs.Add(new Vector2(u, tv)); norms.Add(new Vector3(nx, ny, nz));
                bMin = Vector3.ComponentMin(bMin, vert); bMax = Vector3.ComponentMax(bMax, vert);
                vi3[triLine - 1] = idx;
                triLine++;
 
                if (triLine == 4)
                {
                    faces.Add(new MeshFace(new[]{vi3[0],vi3[1],vi3[2]},
                                           new[]{vi3[0],vi3[1],vi3[2]},
                                           new[]{vi3[0],vi3[1],vi3[2]}));
                    triLine = 1;
                }
            }
            return (verts, uvs, norms, faces, bMin, bMax);
        }
        static float F(string s) {
            float.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float v); return v;
        }
    }
 
    // --------------------------------------------------------------------------
    // MDL  Valve GoldSrc (Half-Life 1) compiled model  ? geometry only
    // --------------------------------------------------------------------------
    public static class MdlLoader
    {
        const int IDST = 0x54534449; // "IDST"
 
        public static (List<Vector3> v, List<Vector2> uv, List<Vector3> n, List<MeshFace> f, Vector3 bMin, Vector3 bMax)
            Load(string path)
        {
            var verts = new List<Vector3>(); var uvs = new List<Vector2>();
            var norms = new List<Vector3>(); var faces = new List<MeshFace>();
            var bMin  = new Vector3( float.MaxValue,  float.MaxValue,  float.MaxValue);
            var bMax  = new Vector3(-float.MaxValue, -float.MaxValue, -float.MaxValue);
 
            byte[] data = File.ReadAllBytes(path);
            if (data.Length < 244) throw new Exception("File too small to be a GoldSrc MDL.");
            if (BitConverter.ToInt32(data, 0) != IDST) throw new Exception("Not a GoldSrc MDL (bad magic). Source Engine MDL is not supported.");
            int version = BitConverter.ToInt32(data, 4);
            if (version != 10) throw new Exception($"Unsupported MDL version {version}. Only GoldSrc v10 is supported.");
 
            // studiohdr_t offsets (all int32)
            int numTextures   = I32(data, 180), texOff    = I32(data, 184);
            int numBodyParts  = I32(data, 204), bpOff     = I32(data, 208);
 
            // Collect texture dimensions for UV scaling
            var texWidths  = new int[Math.Max(1, numTextures)];
            var texHeights = new int[Math.Max(1, numTextures)];
            for (int ti = 0; ti < numTextures; ti++)
            {
                int tBase = texOff + ti * 80;
                if (tBase + 80 > data.Length) break;
                texWidths[ti]  = I32(data, tBase + 64);
                texHeights[ti] = I32(data, tBase + 68);
            }
 
            // Walk: bodyparts ? models ? meshes
            for (int bpi = 0; bpi < numBodyParts; bpi++)
            {
                int bpBase    = bpOff + bpi * 76;
                if (bpBase + 76 > data.Length) break;
                int numModels = I32(data, bpBase + 64);
                int modelOff  = I32(data, bpBase + 68) + bpBase; // relative to bodypart
 
                // Actually the offset is absolute in the file
                modelOff = I32(data, bpBase + 68);
 
                for (int mi = 0; mi < numModels; mi++)
                {
                    // mstudiomodel_t = 112 bytes
                    int mBase    = modelOff + mi * 112;
                    if (mBase + 112 > data.Length) break;
                    int numMesh  = I32(data, mBase + 68);
                    int meshOff  = I32(data, mBase + 72);
                    int numVert  = I32(data, mBase + 76);
                    int vertOff  = I32(data, mBase + 84);   // vec3 array
                    int normOff  = I32(data, mBase + 100);  // vec3 array
 
                    // Read model-local vertices and normals
                    var mVerts = new List<Vector3>();
                    var mNorms = new List<Vector3>();
                    for (int vi = 0; vi < numVert; vi++)
                    {
                        int vo = vertOff + vi * 12;
                        if (vo + 12 > data.Length) break;
                        mVerts.Add(new Vector3(F32(data,vo), F32(data,vo+4), F32(data,vo+8)));
                    }
                    int numNorm = I32(data, mBase + 92);
                    for (int ni = 0; ni < numNorm; ni++)
                    {
                        int no = normOff + ni * 12;
                        if (no + 12 > data.Length) break;
                        mNorms.Add(new Vector3(F32(data,no), F32(data,no+4), F32(data,no+8)));
                    }
 
                    for (int meshi = 0; meshi < numMesh; meshi++)
                    {
                        // mstudiomesh_t = 20 bytes
                        int meshBase = meshOff + meshi * 20;
                        if (meshBase + 20 > data.Length) break;
                        int triOff   = I32(data, meshBase + 4);
                        int skinRef  = I32(data, meshBase + 8);
 
                        int tw = skinRef < texWidths.Length  ? texWidths[skinRef]  : 256;
                        int th = skinRef < texHeights.Length ? texHeights[skinRef] : 256;
 
                        // Read triangle strip/fan data
                        int tPos = triOff;
                        while (tPos + 2 <= data.Length)
                        {
                            short cmd = BitConverter.ToInt16(data, tPos); tPos += 2;
                            if (cmd == 0) break;
                            bool isFan = cmd > 0;
                            int  cnt   = Math.Abs(cmd);
                            var  strip = new List<(int vi, int ni, float u, float v)>();
                            for (int si = 0; si < cnt && tPos + 8 <= data.Length; si++, tPos += 8)
                            {
                                int  svi = BitConverter.ToInt16(data, tPos);
                                int  sni = BitConverter.ToInt16(data, tPos + 2);
                                float su = BitConverter.ToInt16(data, tPos + 4) / (float)tw;
                                float sv = BitConverter.ToInt16(data, tPos + 6) / (float)th;
                                strip.Add((svi, sni, su, sv));
                            }
 
                            // Triangulate strip or fan
                            int baseIdx = verts.Count;
                            foreach (var (svi,sni,su,sv) in strip)
                            {
                                var wv = svi < mVerts.Count ? mVerts[svi] : Vector3.Zero;
                                verts.Add(wv); uvs.Add(new Vector2(su,sv));
                                norms.Add(sni < mNorms.Count ? mNorms[sni] : Vector3.UnitY);
                                bMin = Vector3.ComponentMin(bMin,wv); bMax = Vector3.ComponentMax(bMax,wv);
                            }
                            for (int si = 2; si < strip.Count; si++)
                            {
                                int a = baseIdx, b = baseIdx + si - 1, c = baseIdx + si;
                                if (!isFan && (si & 1) == 1) { int tmp=b; b=c; c=tmp; }
                                else if (isFan) a = baseIdx;
                                faces.Add(new MeshFace(new[]{a,b,c},new[]{a,b,c},new[]{a,b,c}));
                            }
                        }
                    }
                }
            }
            return (verts, uvs, norms, faces, bMin, bMax);
        }
        static int   I32(byte[] d, int o) => BitConverter.ToInt32(d, o);
        static float F32(byte[] d, int o) => BitConverter.ToSingle(d, o);
    }
 
    // --------------------------------------------------------------------------
    // 3DS  Autodesk 3D Studio binary chunk format (.3ds)
    // --------------------------------------------------------------------------
    public static class ThreeDsLoader
    {
        const ushort MAIN3DS=0x4D4D, EDIT3DS=0x3D3D, OBJECT=0x4000,
                     TRIMESH=0x4100, VERTLIST=0x4110, FACELIST=0x4120, MAPLIST=0x4140;
 
        public static (List<Vector3> v, List<Vector2> uv, List<Vector3> n, List<MeshFace> f, Vector3 bMin, Vector3 bMax)
            Load(string path)
        {
            var verts = new List<Vector3>(); var uvs = new List<Vector2>();
            var norms = new List<Vector3>(); var faces = new List<MeshFace>();
            var bMin  = new Vector3( float.MaxValue,  float.MaxValue,  float.MaxValue);
            var bMax  = new Vector3(-float.MaxValue, -float.MaxValue, -float.MaxValue);
 
            byte[] data = File.ReadAllBytes(path);
            if (data.Length < 6 || BitConverter.ToUInt16(data,0) != MAIN3DS)
                throw new Exception("Not a valid .3DS file.");
 
            ParseChunks(data, 6, data.Length, verts, uvs, norms, faces, ref bMin, ref bMax);
            return (verts, uvs, norms, faces, bMin, bMax);
        }
 
        static void ParseChunks(byte[] d, int pos, int end,
            List<Vector3> verts, List<Vector2> uvs, List<Vector3> norms,
            List<MeshFace> faces, ref Vector3 bMin, ref Vector3 bMax)
        {
            while (pos + 6 <= end)
            {
                ushort id  = BitConverter.ToUInt16(d, pos);
                int    len = BitConverter.ToInt32(d, pos + 2);
                if (len < 6) break;
                int chunkEnd = Math.Min(pos + len, end);
                int dataStart = pos + 6;
 
                switch (id)
                {
                    case EDIT3DS:
                        ParseChunks(d, dataStart, chunkEnd, verts, uvs, norms, faces, ref bMin, ref bMax);
                        break;
                    case OBJECT:
                        // Skip object name (null-terminated) then recurse
                        int nameEnd = dataStart;
                        while (nameEnd < chunkEnd && d[nameEnd] != 0) nameEnd++;
                        ParseChunks(d, nameEnd + 1, chunkEnd, verts, uvs, norms, faces, ref bMin, ref bMax);
                        break;
                    case TRIMESH:
                        ParseChunks(d, dataStart, chunkEnd, verts, uvs, norms, faces, ref bMin, ref bMax);
                        break;
                    case VERTLIST:
                    {
                        int cnt = BitConverter.ToUInt16(d, dataStart);
                        int p2 = dataStart + 2;
                        for (int i = 0; i < cnt && p2 + 12 <= chunkEnd; i++, p2 += 12)
                        {
                            var vt = new Vector3(BitConverter.ToSingle(d,p2), BitConverter.ToSingle(d,p2+8), -BitConverter.ToSingle(d,p2+4));
                            verts.Add(vt); uvs.Add(Vector2.Zero);
                            bMin = Vector3.ComponentMin(bMin,vt); bMax = Vector3.ComponentMax(bMax,vt);
                        }
                        break;
                    }
                    case FACELIST:
                    {
                        int cnt = BitConverter.ToUInt16(d, dataStart);
                        int p2 = dataStart + 2;
                        int vBase = verts.Count - (chunkEnd > 0 ? 0 : 0); // face indices are local to this mesh
                        // Find the vertex base for this mesh by tracking previously added verts
                        // (3DS face indices are 0-based within this mesh block, which already added verts above)
                        // We compute base as: total verts minus the count from the preceding VERTLIST
                        // Approximation: use 0-based into whatever was loaded most recently
                        for (int i = 0; i < cnt && p2 + 8 <= chunkEnd; i++, p2 += 8)
                        {
                            int a = BitConverter.ToUInt16(d,p2);
                            int b = BitConverter.ToUInt16(d,p2+2);
                            int c = BitConverter.ToUInt16(d,p2+4);
                            faces.Add(new MeshFace(new[]{a,b,c},new[]{a,b,c},new[]{-1,-1,-1}));
                        }
                        break;
                    }
                    case MAPLIST:
                    {
                        int cnt = BitConverter.ToUInt16(d, dataStart);
                        int p2 = dataStart + 2;
                        // Replace the last 'cnt' UV entries (added by the VERTLIST before this)
                        int uvStart = uvs.Count - cnt;
                        for (int i = 0; i < cnt && p2 + 8 <= chunkEnd; i++, p2 += 8)
                        {
                            float u = BitConverter.ToSingle(d,p2), tv = BitConverter.ToSingle(d,p2+4);
                            if (uvStart + i >= 0 && uvStart + i < uvs.Count)
                                uvs[uvStart + i] = new Vector2(u, tv);
                        }
                        break;
                    }
                }
                pos = chunkEnd;
            }
        }
    }
 
    // --------------------------------------------------------------------------
    // FBX Binary  (Kaydara FBX Binary 7.x)
    // Parses the node tree, finds Geometry nodes and extracts:
    //   Vertices, PolygonVertexIndex, LayerElementNormal, LayerElementUV
    // --------------------------------------------------------------------------
    public static class FbxBinaryLoader
    {
        public static (List<Vector3> v, List<Vector2> uv, List<Vector3> n, List<MeshFace> f, Vector3 bMin, Vector3 bMax)
            Load(string path)
        {
            var verts = new List<Vector3>(); var uvs = new List<Vector2>();
            var norms = new List<Vector3>(); var faces = new List<MeshFace>();
            var bMin  = new Vector3( float.MaxValue,  float.MaxValue,  float.MaxValue);
            var bMax  = new Vector3(-float.MaxValue, -float.MaxValue, -float.MaxValue);
 
            byte[] data = File.ReadAllBytes(path);
            // Validate magic
            if (data.Length < 27 || System.Text.Encoding.ASCII.GetString(data,0,18) != "Kaydara FBX Binary")
                throw new Exception("Not a binary FBX file.");
 
            uint version = BitConverter.ToUInt32(data, 23); // e.g. 7400
            bool wide = version >= 7500;  // uses 64-bit node offsets
            int headerSize = wide ? 25 : 13;  // per-node header
 
            // Walk top-level nodes looking for Objects
            int pos = 27;
            while (pos < data.Length - 13)
            {
                var node = ReadNode(data, ref pos, wide);
                if (node == null) break;
                if (node.Name == "Objects")
                {
                    // Find Geometry children
                    int childPos = node.ChildrenStart;
                    while (childPos < node.End - 13)
                    {
                        var child = ReadNode(data, ref childPos, wide);
                        if (child == null) break;
                        if (child.Name == "Geometry")
                            ExtractGeometry(data, child, wide, verts, uvs, norms, faces, ref bMin, ref bMax);
                    }
                }
            }
            return (verts, uvs, norms, faces, bMin, bMax);
        }
 
        class FbxNode { public string Name; public int ChildrenStart, End; }
 
        static FbxNode ReadNode(byte[] d, ref int pos, bool wide)
        {
            if (pos + (wide ? 25 : 13) > d.Length) return null;
            long endOff  = wide ? (long)BitConverter.ToUInt64(d,pos) : BitConverter.ToUInt32(d,pos);
            long numProp = wide ? (long)BitConverter.ToUInt64(d,pos+8) : BitConverter.ToUInt32(d,pos+4);
            long propLen = wide ? (long)BitConverter.ToUInt64(d,pos+16) : BitConverter.ToUInt32(d,pos+8);
            int  nameLen = d[pos + (wide?24:12)];
            pos += wide ? 25 : 13;
            if (endOff == 0) return null;  // null-record sentinel
 
            string name = System.Text.Encoding.ASCII.GetString(d, pos, nameLen);
            pos += nameLen;
 
            var node = new FbxNode { Name = name, End = (int)endOff };
 
            // Skip over all properties
            pos += (int)propLen;
            node.ChildrenStart = pos;
 
            pos = (int)endOff;
            return node;
        }
 
        static void ExtractGeometry(byte[] d, FbxNode geo, bool wide,
            List<Vector3> verts, List<Vector2> uvs, List<Vector3> norms,
            List<MeshFace> faces, ref Vector3 bMin, ref Vector3 bMax)
        {
            // Read child nodes of this Geometry block properly (we need property data)
            double[] rawVerts = null;
            int[]    polyIdx  = null;
            double[] rawNorms = null, rawUVs = null;
            int[]    normIdx  = null, uvIdx  = null;
            string   normMapping = "ByPolygonVertex", uvMapping = "ByPolygonVertex";
            string   normRef     = "Direct",          uvRef     = "IndexToDirect";
 
            int pos = geo.ChildrenStart;
            while (pos < geo.End - (wide ? 25 : 13))
            {
                int savedPos = pos;
                var child = ReadNodeWithProps(d, ref pos, wide, out var props);
                if (child == null) break;
 
                switch (child.Name)
                {
                    case "Vertices":
                        rawVerts = GetDoubleArray(d, savedPos, wide);  break;
                    case "PolygonVertexIndex":
                        polyIdx  = GetIntArray(d, savedPos, wide);     break;
                    case "LayerElementNormal":
                        ReadLayerElement(d, child, wide, "Normals", out rawNorms, out normIdx, out normMapping, out normRef);
                        break;
                    case "LayerElementUV":
                        ReadLayerElement(d, child, wide, "UV", out rawUVs, out uvIdx, out uvMapping, out uvRef);
                        break;
                }
            }
 
            if (rawVerts == null || polyIdx == null) return;
 
            // Build per-polygon-vertex lists
            int vBase = verts.Count;
            // Add all source vertices
            for (int i = 0; i + 2 < rawVerts.Length; i += 3)
            {
                var pt = new Vector3((float)rawVerts[i], (float)rawVerts[i+1], (float)rawVerts[i+2]);
                verts.Add(pt); uvs.Add(Vector2.Zero); norms.Add(Vector3.UnitY);
                bMin = Vector3.ComponentMin(bMin,pt); bMax = Vector3.ComponentMax(bMax,pt);
            }
 
            // Parse polygon list ? triangles
            var poly = new List<int>();
            int pvIdx = 0;
            for (int i = 0; i < polyIdx.Length; i++)
            {
                int idx = polyIdx[i];
                bool last = idx < 0;
                if (last) idx = ~idx;
 
                // Resolve normal and UV for this polygon vertex
                int normI = ResolveLayerIdx(normMapping, normRef, normIdx, pvIdx, idx, rawNorms?.Length/3 ?? 0);
                int uvI   = ResolveLayerIdx(uvMapping,   uvRef,   uvIdx,   pvIdx, idx, rawUVs?.Length/2   ?? 0);
 
                // Write UV/Normal into the vertex slot (per polygon-vertex override)
                // We create a unique vertex for each polygon-vertex to support per-face attributes
                int newV = vBase + idx;
                if (rawUVs   != null && uvI >= 0 && uvI*2+1 < rawUVs.Length)
                    uvs[newV] = new Vector2((float)rawUVs[uvI*2], (float)rawUVs[uvI*2+1]);
                if (rawNorms != null && normI >= 0 && normI*3+2 < rawNorms.Length)
                    norms[newV] = new Vector3((float)rawNorms[normI*3], (float)rawNorms[normI*3+1], (float)rawNorms[normI*3+2]);
 
                poly.Add(vBase + idx);
                pvIdx++;
 
                if (last)
                {
                    for (int k = 1; k < poly.Count - 1; k++)
                        faces.Add(new MeshFace(new[]{poly[0],poly[k],poly[k+1]},
                                               new[]{poly[0],poly[k],poly[k+1]},
                                               new[]{poly[0],poly[k],poly[k+1]}));
                    poly.Clear();
                }
            }
        }
 
        static int ResolveLayerIdx(string mapping, string reference, int[] idxArr, int pvIdx, int vertIdx, int dataCount)
        {
            int raw = mapping == "ByPolygonVertex" ? pvIdx : vertIdx;
            if (raw >= dataCount) raw = dataCount > 0 ? dataCount - 1 : 0;
            if (reference == "IndexToDirect" && idxArr != null)
                raw = raw < idxArr.Length ? idxArr[raw] : 0;
            return raw;
        }
 
        static void ReadLayerElement(byte[] d, FbxNode el, bool wide, string arrayNodeName,
            out double[] outArr, out int[] outIdx, out string mapping, out string reference)
        {
            outArr = null; outIdx = null; mapping = "ByPolygonVertex"; reference = "Direct";
            int pos = el.ChildrenStart;
            while (pos < el.End - (wide ? 25 : 13))
            {
                int sp = pos;
                var child = ReadNodeWithProps(d, ref pos, wide, out _);
                if (child == null) break;
                if (child.Name == arrayNodeName)           outArr  = GetDoubleArray(d, sp, wide);
                else if (child.Name == arrayNodeName+"Index") outIdx = GetIntArray(d, sp, wide);
                else if (child.Name == "MappingInformationType") mapping   = GetString(d, sp, wide);
                else if (child.Name == "ReferenceInformationType") reference = GetString(d, sp, wide);
            }
        }
 
        // -- Property readers -------------------------------------------------
        // Each of these seeks to the first property of the named node and reads its value.
        static double[] GetDoubleArray(byte[] d, int nodeStart, bool wide)
        {
            int pos = nodeStart;
            SkipNodeHeader(d, ref pos, wide);
            if (pos >= d.Length) return null;
            char t = (char)d[pos]; pos++;
            if (t == 'd') return ReadArrayProp<double>(d, ref pos, 8, BitConverter.ToDouble);
            if (t == 'f') { var fa = ReadArrayProp<float>(d, ref pos, 4, (b,o)=>BitConverter.ToSingle(b,o)); return System.Array.ConvertAll(fa, x=>(double)x); }
            return null;
        }
        static int[] GetIntArray(byte[] d, int nodeStart, bool wide)
        {
            int pos = nodeStart;
            SkipNodeHeader(d, ref pos, wide);
            if (pos >= d.Length) return null;
            char t = (char)d[pos]; pos++;
            if (t == 'i') return ReadArrayProp<int>(d, ref pos, 4, BitConverter.ToInt32);
            if (t == 'l') { var la = ReadArrayProp<long>(d, ref pos, 8, BitConverter.ToInt64); return System.Array.ConvertAll(la, x=>(int)x); }
            return null;
        }
        static string GetString(byte[] d, int nodeStart, bool wide)
        {
            int pos = nodeStart;
            SkipNodeHeader(d, ref pos, wide);
            if (pos >= d.Length) return "";
            char t = (char)d[pos]; pos++;
            if (t == 'S') { int len = BitConverter.ToInt32(d,pos); pos+=4; return System.Text.Encoding.ASCII.GetString(d,pos,len); }
            return "";
        }
 
        static void SkipNodeHeader(byte[] d, ref int pos, bool wide)
        {
            long endOff  = wide ? (long)BitConverter.ToUInt64(d,pos) : BitConverter.ToUInt32(d,pos);
            long numProp = wide ? (long)BitConverter.ToUInt64(d,pos+8) : BitConverter.ToUInt32(d,pos+4);
            pos += wide ? 25 : 13;
            int nameLen = d[pos-1]; pos += nameLen;
            // pos now at first property
        }
 
        static T[] ReadArrayProp<T>(byte[] d, ref int pos, int elemSize, Func<byte[],int,T> conv)
        {
            if (pos + 12 > d.Length) return new T[0];
            int count    = BitConverter.ToInt32(d, pos); pos += 4;
            int encoding = BitConverter.ToInt32(d, pos); pos += 4;
            int compLen  = BitConverter.ToInt32(d, pos); pos += 4;
            byte[] raw;
            if (encoding == 1)
            {
                // zlib-deflate compressed (skip 2 zlib header bytes)
                raw = new byte[count * elemSize];
                try
                {
                    using (var ms  = new MemoryStream(d, pos + 2, compLen - 2))
                    using (var def = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionMode.Decompress))
                    {
                        int read = 0, total = raw.Length;
                        while (read < total) { int r = def.Read(raw, read, total-read); if(r==0)break; read+=r; }
                    }
                }
                catch { }
                pos += compLen;
            }
            else
            {
                int byteCount = count * elemSize;
                raw = new byte[byteCount];
                System.Buffer.BlockCopy(d, pos, raw, 0, Math.Min(byteCount, d.Length - pos));
                pos += byteCount;
            }
            var result = new T[count];
            for (int i = 0; i < count; i++) result[i] = conv(raw, i * elemSize);
            return result;
        }
 
        static FbxNode ReadNodeWithProps(byte[] d, ref int pos, bool wide, out object[] props)
        {
            props = null;
            if (pos + (wide ? 25 : 13) > d.Length) return null;
            long endOff  = wide ? (long)BitConverter.ToUInt64(d,pos) : BitConverter.ToUInt32(d,pos);
            long numProp = wide ? (long)BitConverter.ToUInt64(d,pos+8) : BitConverter.ToUInt32(d,pos+4);
            long propLen = wide ? (long)BitConverter.ToUInt64(d,pos+16) : BitConverter.ToUInt32(d,pos+8);
            int nameLen  = d[pos + (wide?24:12)];
            pos += wide ? 25 : 13;
            if (endOff == 0) return null;
            string name = System.Text.Encoding.ASCII.GetString(d, pos, nameLen);
            pos += nameLen;
            var node = new FbxNode { Name = name, End = (int)endOff };
            pos += (int)propLen;
            node.ChildrenStart = pos;
            pos = (int)endOff;
            return node;
        }
    }
 
 
// =============================================================================
//  CSV COLUMN PICKER DIALOG
//  Shown before loading any .csv file so the user can confirm or override
//  which zero-based column indices hold X, Y, Z, U, V data.
// =============================================================================
 
    public class CsvColumnsDialog : Form
    {
        public int  ColX, ColY, ColZ, ColU, ColV;
        public bool HasHeader;
        public bool Confirmed;
 
        private static readonly Color BG   = Color.FromArgb(240, 240, 240);
        private static readonly Color IDLE  = Color.FromArgb(225, 225, 225);
        private static readonly Color PRESS = Color.FromArgb(204, 228, 247);
        private static readonly Color BORD  = Color.FromArgb(173, 173, 173);
 
        public CsvColumnsDialog(string csvPath,
            int defX=2, int defY=3, int defZ=4, int defU=5, int defV=6, bool defHdr=true)
        {
            Text            = "CSV Column Mapping";
            ClientSize      = new Size(428, 388);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = MinimizeBox = false;
            StartPosition   = FormStartPosition.CenterParent;
            BackColor       = BG;
            Font            = new Font("Segoe UI", 9f);
            KeyPreview      = true;
            KeyDown        += (s, e) => { if (e.KeyCode == Keys.Escape) { Confirmed = false; Close(); } };
 
            // Title + filename
            Add(new Label { Text = "CSV Column Mapping:", Location = new Point(14, 12),
                Font = new Font("Segoe UI", 9f, FontStyle.Bold), AutoSize = true });
            string fn = Path.GetFileName(csvPath);
            Add(new Label { Text = fn.Length > 56 ? fn.Substring(0, 53) + "..." : fn,
                Location = new Point(14, 32), Size = new Size(400, 18),
                ForeColor = Color.FromArgb(0, 80, 160) });
 
            // Header-row preview (shows column indices)
            var headers = CsvLoader.ReadHeaders(csvPath);
            var previewParts = new System.Text.StringBuilder();
            for (int i = 0; i < Math.Min(headers.Length, 12); i++)
                previewParts.Append($"[{i}]{headers[i].Trim()}  ");
            Add(new TextBox { Text = previewParts.ToString().TrimEnd(),
                Location = new Point(14, 58), Size = new Size(400, 50),
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Horizontal,
                BackColor = Color.FromArgb(250, 250, 250),
                Font = new Font("Consolas", 8f), BorderStyle = BorderStyle.FixedSingle,
                WordWrap = false });
 
            Add(new Label { Text = "Select the 0-based column index for each field:",
                Location = new Point(14, 118), Size = new Size(400, 18) });
 
            // -- Spinners ----------------------------------------------------
            int sy = 142;
            var numX = Spin("X  (right)",  defX,  14, sy);
            var numY = Spin("Y  (up)",     defY, 148, sy);
            var numZ = Spin("Z  (depth)",  defZ, 282, sy);
            sy += 66;
            var numU = Spin("U  (tex-X)",  defU,  14, sy);
            var numV = Spin("V  (tex-Y)",  defV, 148, sy);
 
            // -- Header checkbox ---------------------------------------------
            var chkHdr = new CheckBox { Text = "First row is a header (skip it)",
                Checked = defHdr, Location = new Point(14, sy + 54), AutoSize = true };
            Add(chkHdr);
 
            // -- Info tip ----------------------------------------------------
            Add(new Label { Text = "Z is mirrored  ?  V is flipped automatically.",
                Location = new Point(14, sy + 80), Size = new Size(400, 18),
                ForeColor = Color.FromArgb(110, 110, 110) });
 
            // -- Buttons -----------------------------------------------------
            int by = ClientSize.Height - 50;
            var okBtn  = Btn("Load",   14,  by, 190);
            var canBtn = Btn("Cancel", 218, by, 196);
 
            okBtn.Click += (s, e) =>
            {
                ColX = (int)numX.Value; ColY = (int)numY.Value; ColZ = (int)numZ.Value;
                ColU = (int)numU.Value; ColV = (int)numV.Value;
                HasHeader = chkHdr.Checked;
                Confirmed = true;
                Close();
            };
            canBtn.Click += (s, e) => { Confirmed = false; Close(); };
            AcceptButton = okBtn;
        }
 
        private void Add(Control c) { c.BackColor = c.BackColor; Controls.Add(c); }
 
        private NumericUpDown Spin(string label, int def, int x, int y)
        {
            Controls.Add(new Label { Text = label, Location = new Point(x, y),
                Size = new Size(125, 16), BackColor = BG });
            var n = new NumericUpDown { Minimum = 0, Maximum = 999, Value = def,
                Location = new Point(x, y + 20), Size = new Size(120, 24),
                BorderStyle = BorderStyle.FixedSingle };
            Controls.Add(n); return n;
        }
 
        private Button Btn(string text, int x, int y, int width)
        {
            var b = new Button { Text = text, Location = new Point(x, y),
                Size = new Size(width, 32), FlatStyle = FlatStyle.Flat, BackColor = IDLE };
            b.FlatAppearance.BorderColor = BORD;
            b.MouseEnter += (s, e) => b.BackColor = Color.FromArgb(229, 241, 251);
            b.MouseLeave += (s, e) => b.BackColor = IDLE;
            b.MouseDown  += (s, e) => b.BackColor = PRESS;
            Controls.Add(b); return b;
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
