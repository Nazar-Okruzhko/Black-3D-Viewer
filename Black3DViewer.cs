using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using OpenTK;
using OpenTK.Graphics.OpenGL;
using OpenTK.Graphics;

namespace Viewer3D
{
    public class Shader
    {
        public int ProgramId;
        private Dictionary<string, int> uniformCache = new Dictionary<string, int>();
        
        public Shader(string vertexSrc, string fragmentSrc)
        {
            int vertId = CompileShader(ShaderType.VertexShader, vertexSrc);
            int fragId = CompileShader(ShaderType.FragmentShader, fragmentSrc);
            ProgramId = GL.CreateProgram();
            GL.AttachShader(ProgramId, vertId);
            GL.AttachShader(ProgramId, fragId);
            GL.LinkProgram(ProgramId);
            
            GL.GetProgram(ProgramId, GetProgramParameterName.LinkStatus, out int status);
            if (status == 0)
                throw new Exception("Shader link failed: " + GL.GetProgramInfoLog(ProgramId));
            
            GL.DeleteShader(vertId);
            GL.DeleteShader(fragId);
        }
        
        private int CompileShader(ShaderType type, string src)
        {
            int id = GL.CreateShader(type);
            GL.ShaderSource(id, src);
            GL.CompileShader(id);
            GL.GetShader(id, ShaderParameter.CompileStatus, out int status);
            if (status == 0)
                throw new Exception($"{type} compile failed: {GL.GetShaderInfoLog(id)}");
            return id;
        }
        
        public void Use() => GL.UseProgram(ProgramId);
        
        public int GetUniformLocation(string name)
        {
            if (!uniformCache.TryGetValue(name, out int loc))
            {
                loc = GL.GetUniformLocation(ProgramId, name);
                uniformCache[name] = loc;
            }
            return loc;
        }
        
        public void SetInt(string name, int val) => GL.Uniform1(GetUniformLocation(name), val);
        public void SetFloat(string name, float val) => GL.Uniform1(GetUniformLocation(name), val);
        public void SetVec2(string name, Vector2 val) => GL.Uniform2(GetUniformLocation(name), val);
        public void SetVec3(string name, Vector3 val) => GL.Uniform3(GetUniformLocation(name), val);
        public void SetMat4(string name, ref Matrix4 val) => GL.UniformMatrix4(GetUniformLocation(name), false, ref val);
    }
    
    public static class ShaderSource
    {
        public const string VertexShader = @"
#version 330 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aTexCoord;
layout(location = 3) in vec3 aTangent;

out vec3 FragPos;
out vec3 Normal;
out vec2 TexCoord;
out mat3 TBN;
out vec2 ScreenPos;

uniform mat4 model;
uniform mat4 view;
uniform mat4 projection;

void main()
{
    FragPos = vec3(model * vec4(aPos, 1.0));
    Normal = mat3(transpose(inverse(model))) * aNormal;
    TexCoord = aTexCoord;
    
    vec3 T = normalize(mat3(model) * aTangent);
    vec3 N = normalize(Normal);
    T = normalize(T - dot(T, N) * N);
    vec3 B = cross(N, T);
    TBN = mat3(T, B, N);
    
    gl_Position = projection * view * vec4(FragPos, 1.0);
    ScreenPos = gl_Position.xy / gl_Position.w;
}
";

        public const string FragmentShader = @"
#version 330 core
out vec4 FragColor;

in vec3 FragPos;
in vec3 Normal;
in vec2 TexCoord;
in mat3 TBN;
in vec2 ScreenPos;

uniform vec3 lightPos;
uniform vec3 lightPos2;
uniform vec3 viewPos;

uniform sampler2D colorMap;
uniform sampler2D normalMap;
uniform sampler2D specularMap;
uniform sampler2D roughnessMap;
uniform sampler2D metallicMap;
uniform sampler2D opacityMap;

uniform bool hasColorMap;
uniform bool hasNormalMap;
uniform bool hasSpecularMap;
uniform bool hasRoughnessMap;
uniform bool hasMetallicMap;
uniform bool hasOpacityMap;

uniform int shadingMode;
uniform vec3 solidColor;

vec3 calcLight(vec3 lightPos, vec3 lightColor, vec3 norm, vec3 viewDir, vec3 albedo, float roughness, float metallic, float specularStrength)
{
    vec3 lightDir = normalize(lightPos - FragPos);
    float diff = max(dot(norm, lightDir), 0.0);
    
    vec3 halfDir = normalize(lightDir + viewDir);
    float spec = pow(max(dot(norm, halfDir), 0.0), (1.0 - roughness) * 64.0 + 1.0);
    
    vec3 F0 = mix(vec3(0.04), albedo, metallic);
    vec3 F = F0 + (1.0 - F0) * pow(1.0 - max(dot(halfDir, viewDir), 0.0), 5.0);
    
    vec3 kD = (1.0 - F) * (1.0 - metallic);
    
    vec3 ambient = 0.35 * lightColor * albedo;
    vec3 diffuse = kD * diff * lightColor * albedo * 1.0;
    vec3 specular = F * spec * specularStrength * lightColor * 0.7;
    
    float distance = length(lightPos - FragPos);
    float attenuation = 1.0 / (1.0 + 0.01 * distance + 0.001 * distance * distance);
    
    return (ambient + (diffuse + specular) * attenuation);
}
void main()
{
    if (shadingMode == 1) {
        FragColor = vec4(0.96, 0.96, 0.96, 1.0);
        return;
    }
    
    vec3 albedo = solidColor;
    if (hasColorMap)
        albedo = texture(colorMap, TexCoord).rgb;
    
    vec3 norm = normalize(Normal);
    if (hasNormalMap) {
        vec3 normalTex = texture(normalMap, TexCoord).rgb * 2.0 - 1.0;
        norm = normalize(TBN * normalTex);
    }
    
    float roughness = 0.5;
    if (hasRoughnessMap)
        roughness = texture(roughnessMap, TexCoord).r;
    
    float metallic = 0.0;
    if (hasMetallicMap)
        metallic = texture(metallicMap, TexCoord).r;
    
    float specularStrength = 0.5;
    if (hasSpecularMap)
        specularStrength = texture(specularMap, TexCoord).r;
    
    vec3 viewDir = normalize(viewPos - FragPos);
    
    vec3 result = calcLight(lightPos, vec3(1.0, 1.0, 1.0), norm, viewDir, albedo, roughness, metallic, specularStrength);
    result += calcLight(lightPos2, vec3(0.55, 0.55, 0.55), norm, viewDir, albedo, roughness, metallic, specularStrength);
    
    float opacity = 1.0;
    if (hasOpacityMap)
        opacity = texture(opacityMap, TexCoord).r;
    
    float dist = length(ScreenPos);
    float vignette = smoothstep(1.4, 0.5, dist);
    vignette = mix(0.85, 1.0, vignette);
    result *= vignette;
    
    FragColor = vec4(result, opacity);
}
";

        public const string WireframeVertexShader = @"
#version 330 core
layout(location = 0) in vec3 aPos;

out vec2 ScreenPos;

uniform mat4 model;
uniform mat4 view;
uniform mat4 projection;

void main()
{
    gl_Position = projection * view * model * vec4(aPos, 1.0);
    ScreenPos = gl_Position.xy / gl_Position.w;
}
";

        public const string WireframeFragmentShader = @"
#version 330 core
out vec4 FragColor;
in vec2 ScreenPos;
uniform vec3 color;

void main()
{
    float dist = length(ScreenPos);
    float vignette = smoothstep(1.4, 0.5, dist);
    vignette = mix(0.85, 1.0, vignette);
    FragColor = vec4(color * vignette, 1.0);
}
";
    }
    
    public class OBJLoader
    {
        public List<Vector3> Vertices = new List<Vector3>();
        public List<Vector2> TexCoords = new List<Vector2>();
        public List<Vector3> Normals = new List<Vector3>();
        public List<Face> Faces = new List<Face>();
        
        public int ColorMapId = -1;
        public int NormalMapId = -1;
        public int SpecularMapId = -1;
        public int MetallicMapId = -1;
        public int RoughnessMapId = -1;
        public int OpacityMapId = -1;
        
        public bool HasTexture = false;
        public int TextureId = -1;
        
        public Vector3 BoundsMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        public Vector3 BoundsMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        public int EdgeCount { get; private set; }
        
        public int VAO, VBO, EBO;
        private int indexCount;
        
        public class Face
        {
            public int[] VertexIndices;
            public int[] TexCoordIndices;
            public int[] NormalIndices;
        }
        
        public void LoadFromFile(string filename)
        {
            Vertices.Clear();
            Faces.Clear();
            Normals.Clear();
            TexCoords.Clear();
            
            var lines = File.ReadAllLines(filename);
            
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
                    continue;
                    
                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                
                if (parts[0] == "v" && parts.Length >= 4)
                {
                    var vertex = new Vector3(
                        float.Parse(parts[1]),
                        float.Parse(parts[3]),
                        -float.Parse(parts[2])
                    );
                    Vertices.Add(vertex);
                    
                    BoundsMin.X = Math.Min(BoundsMin.X, vertex.X);
                    BoundsMin.Y = Math.Min(BoundsMin.Y, vertex.Y);
                    BoundsMin.Z = Math.Min(BoundsMin.Z, vertex.Z);
                    BoundsMax.X = Math.Max(BoundsMax.X, vertex.X);
                    BoundsMax.Y = Math.Max(BoundsMax.Y, vertex.Y);
                    BoundsMax.Z = Math.Max(BoundsMax.Z, vertex.Z);
                }
                else if (parts[0] == "vt" && parts.Length >= 3)
                {
                    TexCoords.Add(new Vector2(float.Parse(parts[1]), float.Parse(parts[2])));
                }
                else if (parts[0] == "vn" && parts.Length >= 4)
                {
                    Normals.Add(new Vector3(float.Parse(parts[1]), float.Parse(parts[3]), -float.Parse(parts[2])));
                }
                else if (parts[0] == "f" && parts.Length >= 4)
                {
                    var face = new Face
                    {
                        VertexIndices = new int[parts.Length - 1],
                        TexCoordIndices = new int[parts.Length - 1],
                        NormalIndices = new int[parts.Length - 1]
                    };
                    
                    for (int i = 1; i < parts.Length; i++)
                    {
                        var indices = parts[i].Split('/');
                        face.VertexIndices[i - 1] = int.Parse(indices[0]) - 1;
                        face.TexCoordIndices[i - 1] = indices.Length > 1 && !string.IsNullOrEmpty(indices[1]) ? int.Parse(indices[1]) - 1 : 0;
                        face.NormalIndices[i - 1] = indices.Length > 2 && !string.IsNullOrEmpty(indices[2]) ? int.Parse(indices[2]) - 1 : 0;
                    }
                    
                    if (face.VertexIndices.Length > 3)
                    {
                        for (int i = 1; i < face.VertexIndices.Length - 1; i++)
                        {
                            Faces.Add(new Face
                            {
                                VertexIndices = new[] { face.VertexIndices[0], face.VertexIndices[i], face.VertexIndices[i + 1] },
                                TexCoordIndices = new[] { face.TexCoordIndices[0], face.TexCoordIndices[i], face.TexCoordIndices[i + 1] },
                                NormalIndices = new[] { face.NormalIndices[0], face.NormalIndices[i], face.NormalIndices[i + 1] }
                            });
                        }
                    }
                    else
                    {
                        Faces.Add(face);
                    }
                }
            }

            var edgeSet = new HashSet<string>();
            foreach (var face in Faces)
            {
                for (int i = 0; i < face.VertexIndices.Length; i++)
                {
                    int v1 = face.VertexIndices[i];
                    int v2 = face.VertexIndices[(i + 1) % face.VertexIndices.Length];
                    string edge = Math.Min(v1, v2) + "," + Math.Max(v1, v2);
                    edgeSet.Add(edge);
                }
            }
            EdgeCount = edgeSet.Count;
            
            LoadMaterial(filename);
            BuildBuffers();
        }
        
        private void BuildBuffers()
        {
            var vertexData = new List<float>();
            var indices = new List<uint>();
            
            for (int i = 0; i < Faces.Count; i++)
            {
                var face = Faces[i];
                for (int j = 0; j < 3; j++)
                {
                    var pos = Vertices[face.VertexIndices[j]];
                    vertexData.Add(pos.X);
                    vertexData.Add(pos.Y);
                    vertexData.Add(pos.Z);
                    
                    if (face.NormalIndices[j] < Normals.Count)
                    {
                        var norm = Normals[face.NormalIndices[j]];
                        vertexData.Add(norm.X);
                        vertexData.Add(norm.Y);
                        vertexData.Add(norm.Z);
                    }
                    else
                    {
                        vertexData.Add(0f);
                        vertexData.Add(1f);
                        vertexData.Add(0f);
                    }
                    
                    if (face.TexCoordIndices[j] < TexCoords.Count)
                    {
                        var uv = TexCoords[face.TexCoordIndices[j]];
                        vertexData.Add(uv.X);
                        vertexData.Add(uv.Y);
                    }
                    else
                    {
                        vertexData.Add(0f);
                        vertexData.Add(0f);
                    }
                    
                    vertexData.Add(1f);
                    vertexData.Add(0f);
                    vertexData.Add(0f);
                    
                    indices.Add((uint)(i * 3 + j));
                }
            }
            
            ComputeTangents(vertexData, indices);
            
            VAO = GL.GenVertexArray();
            VBO = GL.GenBuffer();
            EBO = GL.GenBuffer();
            
            GL.BindVertexArray(VAO);
            
            GL.BindBuffer(BufferTarget.ArrayBuffer, VBO);
            GL.BufferData(BufferTarget.ArrayBuffer, vertexData.Count * sizeof(float), vertexData.ToArray(), BufferUsageHint.StaticDraw);
            
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, EBO);
            GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Count * sizeof(uint), indices.ToArray(), BufferUsageHint.StaticDraw);
            
            int stride = 11 * sizeof(float);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));
            GL.EnableVertexAttribArray(2);
            GL.VertexAttribPointer(3, 3, VertexAttribPointerType.Float, false, stride, 8 * sizeof(float));
            GL.EnableVertexAttribArray(3);
            
            GL.BindVertexArray(0);
            
            indexCount = indices.Count;
            
            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
            GL.BindVertexArray(0);
        }

        public void RecalculateNormals()
        {
            // Clear existing normals
            Normals.Clear();
            var vertexNormals = new Dictionary<int, List<Vector3>>();
            
            // Initialize vertex normal lists
            for (int i = 0; i < Vertices.Count; i++)
            {
                vertexNormals[i] = new List<Vector3>();
            }
            
            // Calculate face normals and accumulate to vertices
            foreach (var face in Faces)
            {
                Vector3 v0 = Vertices[face.VertexIndices[0]];
                Vector3 v1 = Vertices[face.VertexIndices[1]];
                Vector3 v2 = Vertices[face.VertexIndices[2]];
                
                Vector3 edge1 = v1 - v0;
                Vector3 edge2 = v2 - v0;
                Vector3 faceNormal = Vector3.Cross(edge1, edge2);
                faceNormal.Normalize();
                
                // Add face normal to each vertex of the face
                foreach (int vertexIndex in face.VertexIndices)
                {
                    vertexNormals[vertexIndex].Add(faceNormal);
                }
            }
            
            // Average normals for each vertex
            for (int i = 0; i < Vertices.Count; i++)
            {
                Vector3 avgNormal = Vector3.Zero;
                foreach (var normal in vertexNormals[i])
                {
                    avgNormal += normal;
                }
                avgNormal.Normalize();
                Normals.Add(avgNormal);
            }
            
            // Update face normal indices to point to vertex indices
            foreach (var face in Faces)
            {
                for (int i = 0; i < face.NormalIndices.Length; i++)
                {
                    face.NormalIndices[i] = face.VertexIndices[i];
                }
            }
            
            // Rebuild buffers with new normals
            BuildBuffers();
        }
        
        private void ComputeTangents(List<float> vertexData, List<uint> indices)
        {
            for (int i = 0; i < indices.Count; i += 3)
            {
                int i0 = (int)indices[i] * 11;
                int i1 = (int)indices[i + 1] * 11;
                int i2 = (int)indices[i + 2] * 11;
                
                Vector3 v0 = new Vector3(vertexData[i0], vertexData[i0 + 1], vertexData[i0 + 2]);
                Vector3 v1 = new Vector3(vertexData[i1], vertexData[i1 + 1], vertexData[i1 + 2]);
                Vector3 v2 = new Vector3(vertexData[i2], vertexData[i2 + 1], vertexData[i2 + 2]);
                
                Vector2 uv0 = new Vector2(vertexData[i0 + 6], vertexData[i0 + 7]);
                Vector2 uv1 = new Vector2(vertexData[i1 + 6], vertexData[i1 + 7]);
                Vector2 uv2 = new Vector2(vertexData[i2 + 6], vertexData[i2 + 7]);
                
                Vector3 edge1 = v1 - v0;
                Vector3 edge2 = v2 - v0;
                Vector2 deltaUV1 = uv1 - uv0;
                Vector2 deltaUV2 = uv2 - uv0;
                
                float f = 1.0f / (deltaUV1.X * deltaUV2.Y - deltaUV2.X * deltaUV1.Y + 0.0001f);
                Vector3 tangent = new Vector3(
                    f * (deltaUV2.Y * edge1.X - deltaUV1.Y * edge2.X),
                    f * (deltaUV2.Y * edge1.Y - deltaUV1.Y * edge2.Y),
                    f * (deltaUV2.Y * edge1.Z - deltaUV1.Y * edge2.Z)
                );
                tangent.Normalize();
                
                vertexData[i0 + 8] = tangent.X;
                vertexData[i0 + 9] = tangent.Y;
                vertexData[i0 + 10] = tangent.Z;
                vertexData[i1 + 8] = tangent.X;
                vertexData[i1 + 9] = tangent.Y;
                vertexData[i1 + 10] = tangent.Z;
                vertexData[i2 + 8] = tangent.X;
                vertexData[i2 + 9] = tangent.Y;
                vertexData[i2 + 10] = tangent.Z;
            }
        }
        
        private void LoadMaterial(string objFilename)
        {
            var mtlFile = Path.ChangeExtension(objFilename, ".mtl");
            if (File.Exists(mtlFile))
            {
                var lines = File.ReadAllLines(mtlFile);
                foreach (var line in lines)
                {
                    var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;
                    
                    string mapType = parts[0];
                    string texturePath = Path.Combine(Path.GetDirectoryName(objFilename), parts[1]);
                    
                    if (File.Exists(texturePath))
                    {
                        if (mapType == "map_Kd")
                            ColorMapId = LoadTextureFile(texturePath);
                        else if (mapType == "map_Bump" || mapType == "bump")
                            NormalMapId = LoadTextureFile(texturePath);
                        else if (mapType == "map_Ks")
                            SpecularMapId = LoadTextureFile(texturePath);
                        else if (mapType == "map_Pm")
                            MetallicMapId = LoadTextureFile(texturePath);
                        else if (mapType == "map_Pr" || mapType == "map_Ns")
                            RoughnessMapId = LoadTextureFile(texturePath);
                        else if (mapType == "map_d")
                            OpacityMapId = LoadTextureFile(texturePath);
                    }
                }
                
                if (ColorMapId >= 0)
                {
                    TextureId = ColorMapId;
                    HasTexture = true;
                    return;
                }
            }
            
            var baseDir = Path.GetDirectoryName(objFilename);
            var baseName = Path.GetFileNameWithoutExtension(objFilename);
            var extensions = new[] { ".png", ".jpg", ".jpeg", ".bmp", ".dds", ".tga" };
            
            foreach (var ext in extensions)
            {
                var colorPath = Path.Combine(baseDir, baseName + ext);
                if (File.Exists(colorPath))
                {
                    ColorMapId = LoadTextureFile(colorPath);
                    TextureId = ColorMapId;
                    HasTexture = true;
                    return;
                }
            }
        }
        
        private int LoadTextureFile(string filename)
        {
            try
            {
                Bitmap bitmap = null;
                string ext = Path.GetExtension(filename).ToLower();
                
                if (ext == ".dds")
                {
                    bitmap = LoadDDS(filename);
                }
                else if (ext == ".tga")
                {
                    bitmap = LoadTGA(filename);
                }
                else
                {
                    bitmap = new Bitmap(filename);
                }
                
                if (bitmap == null)
                    return -1;
                
                bitmap.RotateFlip(RotateFlipType.RotateNoneFlipY);
                var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                    System.Drawing.Imaging.ImageLockMode.ReadOnly,
                    System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                
                int texId = GL.GenTexture();
                GL.BindTexture(TextureTarget.Texture2D, texId);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgb, bitmap.Width, bitmap.Height, 0,
                    PixelFormat.Bgr, PixelType.UnsignedByte, data.Scan0);
                GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
                
                bitmap.UnlockBits(data);
                bitmap.Dispose();
                
                return texId;
            }
            catch
            {
                return -1;
            }
        }
        
        private Bitmap LoadDDS(string filename)
        {
            try
            {
                using (var fs = new FileStream(filename, FileMode.Open, FileAccess.Read))
                using (var br = new BinaryReader(fs))
                {
                    uint magic = br.ReadUInt32();
                    if (magic != 0x20534444) return null;
                    
                    br.ReadBytes(4);
                    int height = br.ReadInt32();
                    int width = br.ReadInt32();
                    br.ReadBytes(108 - 16);
                    
                    byte[] pixelData = br.ReadBytes(width * height * 4);
                    
                    Bitmap bmp = new Bitmap(width, height);
                    int idx = 0;
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            byte b = pixelData[idx++];
                            byte g = pixelData[idx++];
                            byte r = pixelData[idx++];
                            byte a = pixelData[idx++];
                            bmp.SetPixel(x, y, Color.FromArgb(a, r, g, b));
                        }
                    }
                    return bmp;
                }
            }
            catch
            {
                return null;
            }
        }
        
        private Bitmap LoadTGA(string filename)
        {
            try
            {
                using (var fs = new FileStream(filename, FileMode.Open, FileAccess.Read))
                using (var br = new BinaryReader(fs))
                {
                    br.ReadBytes(2);
                    byte imageType = br.ReadByte();
                    br.ReadBytes(9);
                    short width = br.ReadInt16();
                    short height = br.ReadInt16();
                    byte bpp = br.ReadByte();
                    br.ReadByte();
                    
                    int bytesPerPixel = bpp / 8;
                    byte[] pixelData = br.ReadBytes(width * height * bytesPerPixel);
                    
                    Bitmap bmp = new Bitmap(width, height);
                    int idx = 0;
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            byte b = pixelData[idx++];
                            byte g = pixelData[idx++];
                            byte r = pixelData[idx++];
                            byte a = bytesPerPixel == 4 ? pixelData[idx++] : (byte)255;
                            bmp.SetPixel(x, y, Color.FromArgb(a, r, g, b));
                        }
                    }
                    return bmp;
                }
            }
            catch
            {
                return null;
            }
        }
        
        public void LoadTexture(string filename, int mapType)
        {
            int texId = LoadTextureFile(filename);
            if (texId >= 0)
            {
                switch (mapType)
                {
                    case 0: ColorMapId = texId; break;
                    case 1: NormalMapId = texId; break;
                    case 2: SpecularMapId = texId; break;
                    case 3: RoughnessMapId = texId; break;
                    case 4: MetallicMapId = texId; break;
                    case 5: OpacityMapId = texId; break;
                }
                SetActiveTexture(mapType);
            }
        }
        
        public void SetActiveTexture(int mapType)
        {
            switch (mapType)
            {
                case 0: if (ColorMapId >= 0) { TextureId = ColorMapId; HasTexture = true; } break;
                case 1: if (NormalMapId >= 0) { TextureId = NormalMapId; HasTexture = true; } break;
                case 2: if (SpecularMapId >= 0) { TextureId = SpecularMapId; HasTexture = true; } break;
                case 3: if (RoughnessMapId >= 0) { TextureId = RoughnessMapId; HasTexture = true; } break;
                case 4: if (MetallicMapId >= 0) { TextureId = MetallicMapId; HasTexture = true; } break;
                case 5: if (OpacityMapId >= 0) { TextureId = OpacityMapId; HasTexture = true; } break;
            }
        }
        
        public float GetModelSize()
        {
            var size = BoundsMax - BoundsMin;
            return Math.Max(Math.Max(size.X, size.Y), size.Z);
        }
        
        public Vector3 GetModelCenter()
        {
            return (BoundsMin + BoundsMax) / 2;
        }
        
        public void Render(Shader shader, int shadingMode)
        {
            shader.Use();
            shader.SetInt("shadingMode", shadingMode);
            
            if (shadingMode == 2)
            {
                shader.SetVec3("solidColor", new Vector3(1f, 1f, 1f));
                
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, ColorMapId >= 0 ? ColorMapId : 0);
                shader.SetInt("colorMap", 0);
                shader.SetInt("hasColorMap", ColorMapId >= 0 ? 1 : 0);
                
                GL.ActiveTexture(TextureUnit.Texture1);
                GL.BindTexture(TextureTarget.Texture2D, NormalMapId >= 0 ? NormalMapId : 0);
                shader.SetInt("normalMap", 1);
                shader.SetInt("hasNormalMap", NormalMapId >= 0 ? 1 : 0);
                
                GL.ActiveTexture(TextureUnit.Texture2);
                GL.BindTexture(TextureTarget.Texture2D, SpecularMapId >= 0 ? SpecularMapId : 0);
                shader.SetInt("specularMap", 2);
                shader.SetInt("hasSpecularMap", SpecularMapId >= 0 ? 1 : 0);
                
                GL.ActiveTexture(TextureUnit.Texture3);
                GL.BindTexture(TextureTarget.Texture2D, RoughnessMapId >= 0 ? RoughnessMapId : 0);
                shader.SetInt("roughnessMap", 3);
                shader.SetInt("hasRoughnessMap", RoughnessMapId >= 0 ? 1 : 0);
                
                GL.ActiveTexture(TextureUnit.Texture4);
                GL.BindTexture(TextureTarget.Texture2D, MetallicMapId >= 0 ? MetallicMapId : 0);
                shader.SetInt("metallicMap", 4);
                shader.SetInt("hasMetallicMap", MetallicMapId >= 0 ? 1 : 0);
                
                GL.ActiveTexture(TextureUnit.Texture5);
                GL.BindTexture(TextureTarget.Texture2D, OpacityMapId >= 0 ? OpacityMapId : 0);
                shader.SetInt("opacityMap", 5);
                shader.SetInt("hasOpacityMap", OpacityMapId >= 0 ? 1 : 0);
            }
            else
            {
                shader.SetVec3("solidColor", new Vector3(1f, 1f, 1f));
                shader.SetInt("hasColorMap", 0);
                shader.SetInt("hasNormalMap", 0);
                shader.SetInt("hasSpecularMap", 0);
                shader.SetInt("hasRoughnessMap", 0);
                shader.SetInt("hasMetallicMap", 0);
                shader.SetInt("hasOpacityMap", 0);
            }
            
            GL.BindVertexArray(VAO);
            GL.DrawElements(PrimitiveType.Triangles, indexCount, DrawElementsType.UnsignedInt, 0);
            GL.BindVertexArray(0);
        }
        
        public void Cleanup()
        {
            if (VAO != 0) GL.DeleteVertexArray(VAO);
            if (VBO != 0) GL.DeleteBuffer(VBO);
            if (EBO != 0) GL.DeleteBuffer(EBO);
        }
    }
    
    public class Viewer3DForm : Form
    {
        private GLControl glControl;
        private TabControl tabControl;
        private TabPage envLightTab, statsShadingTab;
        private Button loadButton;
        private Label verticesLabel, trianglesLabel, edgesLabel, loadedLabel, noTextureLabel;
        private Panel previewPanel, lightControlPanel;
        
        private Button solidButton, wireframeButton, textureButton;
        private Button colorMapButton, normalMapButton, specularMapButton, metallicMapButton, roughnessMapButton, opacityMapButton;
        private Button showUVButton;
        
        private Button showGridButton, showAxesButton;
        private bool showGrid = false, showAxes = false;
        private Vector3 cachedModelCenter = Vector3.Zero;
        private float cachedModelSize = 5f;
        
        private float rotationX = 0f, rotationY = 0f;
        private float zoom = -5f;
        private float panX = 0f, panY = 0f;
        private Vector3 lookAtPoint = Vector3.Zero;
        private float minRotationX = -89f, maxRotationX = 89f;
        private float minZoom = -1f, maxZoom = -100f;
        
        private float lightAngle = 0f;
        private bool draggingLight = false;
        
        private bool draggingRotate = false, draggingPan = false;
        private Point lastMousePos;
        private DateTime lastClickTime = DateTime.MinValue;
        
        private OBJLoader currentModel = null;
        private bool showDefaultCube = true;
        
        private int shadingMode = 0;
        private bool showUVPreview = false;
        private int currentTextureMap = 0;
        
        private Shader mainShader, wireframeShader;
        private int cubeVAO, cubeVBO;
        
        private Color panelBg = Color.FromArgb(240, 240, 240);
        private Color buttonIdle = Color.FromArgb(225, 225, 225);
        private Color buttonHover = Color.FromArgb(229, 241, 251);
        private Color buttonPress = Color.FromArgb(204, 228, 247);
        private Color buttonBorder = Color.FromArgb(173, 173, 173);
        private Color buttonBorderActive = Color.FromArgb(0, 120, 215);
        
        public Viewer3DForm()
        {
            InitializeComponent();
            
            // Load embedded icon
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                using (var stream = asm.GetManifestResourceStream("Viewer3D.1.ico"))
                    if (stream != null) this.Icon = new Icon(stream);
            }
            catch { }
            
            this.Load += (s, e) => SetupGL();
            this.KeyPreview = true;
            this.KeyDown += Viewer3DForm_KeyDown;
            glControl.Resize += (s, e) => { SetupProjection(); glControl.Invalidate(); };
            UpdateLabels();
        }
        
        private void Viewer3DForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.R)
            {
                ResetCamera();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.N)
            {
                if (currentModel != null)
                {
                    currentModel.RecalculateNormals();
                    glControl.Invalidate();
                    MessageBox.Show("Normals recalculated!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                e.Handled = true;
            }
        }
        private void ResetCamera()
        {
            if (currentModel != null)
            {
                lookAtPoint = currentModel.GetModelCenter();
                cachedModelCenter = lookAtPoint;
                cachedModelSize = currentModel.GetModelSize();
                var modelSize = cachedModelSize;
                zoom = -modelSize * 1f;
            }
            else
            {
                zoom = -5f;
            }
            rotationX = 0f;
            rotationY = 0f;
            panX = 0;
            panY = 0;
            glControl.Invalidate();
        }
        
        private void InitializeComponent()
        {
            this.Text = "HotDog - 3D Viewer";
            this.ClientSize = new Size(1200, 800);
            this.MinimumSize = new Size(800, 600);
            this.AllowDrop = true;
            this.DragEnter += Viewer3DForm_DragEnter;
            this.DragDrop += Viewer3DForm_DragDrop;
            
            glControl = new GLControl(new GraphicsMode(32, 24, 0, 4), 3, 3, GraphicsContextFlags.ForwardCompatible);
            glControl.Dock = DockStyle.Fill;
            glControl.Paint += GlControl_Paint;
            glControl.MouseDown += GlControl_MouseDown;
            glControl.MouseUp += GlControl_MouseUp;
            glControl.MouseMove += GlControl_MouseMove;
            glControl.MouseWheel += GlControl_MouseWheel;
            this.Controls.Add(glControl);
            
            loadButton = new Button
            {
                Text = "Load OBJ",
                Location = new Point(10, 10),
                Size = new Size(120, 40),
                FlatStyle = FlatStyle.Flat,
                BackColor = buttonIdle,
                FlatAppearance = { BorderColor = buttonBorder }
            };
            loadButton.Click += LoadButton_Click;
            loadButton.MouseEnter += (s, e) => { loadButton.BackColor = buttonHover; loadButton.FlatAppearance.BorderColor = buttonBorderActive; };
            loadButton.MouseLeave += (s, e) => { loadButton.BackColor = buttonIdle; loadButton.FlatAppearance.BorderColor = buttonBorder; };
            loadButton.MouseDown += (s, e) => loadButton.BackColor = buttonPress;
            loadButton.MouseUp += (s, e) => loadButton.BackColor = buttonHover;
            glControl.Controls.Add(loadButton);
            
            noTextureLabel = new Label
            {
                Text = "No texture found",
                Location = new Point(10, 60),
                Size = new Size(200, 30),
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                ForeColor = Color.FromArgb(200, 50, 50),
                BackColor = Color.Transparent,
                Visible = false
            };
            glControl.Controls.Add(noTextureLabel);
            
            tabControl = new TabControl
            {
                Dock = DockStyle.Right,
                Width = 290,
                Appearance = TabAppearance.FlatButtons
            };
            this.Controls.Add(tabControl);
            
            envLightTab = new TabPage("Env & Light") { BackColor = panelBg };
            tabControl.TabPages.Add(envLightTab);
            
            lightControlPanel = new Panel
            {
                Location = new Point(55, 120),
                Size = new Size(180, 180),
                BackColor = Color.FromArgb(250, 250, 250),
                BorderStyle = BorderStyle.FixedSingle
            };
            lightControlPanel.Paint += LightControlPanel_Paint;
            lightControlPanel.MouseDown += LightControlPanel_MouseDown;
            lightControlPanel.MouseUp += LightControlPanel_MouseUp;
            lightControlPanel.MouseMove += LightControlPanel_MouseMove;
            envLightTab.Controls.Add(lightControlPanel);
            
            statsShadingTab = new TabPage("Stats & Shading") { BackColor = panelBg };
            tabControl.TabPages.Add(statsShadingTab);
            
            int y = 10;
            
            loadedLabel = new Label
            {
                Text = "Default Cube",
                Font = new Font("Segoe UI", 11, FontStyle.Bold),
                Location = new Point(10, y),
                Size = new Size(270, 30),
                BackColor = panelBg
            };
            statsShadingTab.Controls.Add(loadedLabel);
            y += 40;
            
            previewPanel = new Panel
            {
                Location = new Point(10, y),
                Size = new Size(270, 270),
                BackColor = Color.FromArgb(60, 60, 60),
                BorderStyle = BorderStyle.FixedSingle,
                Visible = false
            };
            previewPanel.Paint += PreviewPanel_Paint;
            statsShadingTab.Controls.Add(previewPanel);
            y += 280;
            
            solidButton = AddStyledButton(statsShadingTab, "Solid Shading", ref y);
            solidButton.BackColor = buttonPress;
            solidButton.Click += (s, e) => { shadingMode = 0; UpdateShadingButtons(); UpdateNoTextureLabel(); glControl.Invalidate(); };
            
            wireframeButton = AddStyledButton(statsShadingTab, "Wireframe View", ref y);
            wireframeButton.Click += (s, e) => { shadingMode = 1; UpdateShadingButtons(); UpdateNoTextureLabel(); glControl.Invalidate(); };
            
            textureButton = AddStyledButton(statsShadingTab, "Texture View", ref y);
            textureButton.Click += (s, e) => { shadingMode = 2; UpdateShadingButtons(); UpdateTextureButtonStates(); UpdateNoTextureLabel(); glControl.Invalidate(); };
            y += 10;
            
            var texGroup = new GroupBox
            {
                Text = "Texture Maps",
                Location = new Point(10, y),
                Size = new Size(270, 190),
                BackColor = panelBg
            };
            statsShadingTab.Controls.Add(texGroup);
            
            int gy = 25;
            colorMapButton = AddTextureButton(texGroup, "Color", ref gy);
            colorMapButton.BackColor = buttonPress;
            colorMapButton.Click += (s, e) => { currentTextureMap = 0; if (currentModel != null) currentModel.SetActiveTexture(0); UpdateTextureButtons(); UpdateNoTextureLabel(); glControl.Invalidate(); };
            
            normalMapButton = AddTextureButton(texGroup, "Normal", ref gy);
            normalMapButton.Click += (s, e) => { currentTextureMap = 1; if (currentModel != null) currentModel.SetActiveTexture(1); UpdateTextureButtons(); UpdateNoTextureLabel(); glControl.Invalidate(); };
            
            specularMapButton = AddTextureButton(texGroup, "Specular", ref gy);
            specularMapButton.Click += (s, e) => { currentTextureMap = 2; if (currentModel != null) currentModel.SetActiveTexture(2); UpdateTextureButtons(); UpdateNoTextureLabel(); glControl.Invalidate(); };
            
            roughnessMapButton = AddTextureButton(texGroup, "Roughness", ref gy);
            roughnessMapButton.Click += (s, e) => { currentTextureMap = 3; if (currentModel != null) currentModel.SetActiveTexture(3); UpdateTextureButtons(); UpdateNoTextureLabel(); glControl.Invalidate(); };
            
            metallicMapButton = AddTextureButton(texGroup, "Metallic", ref gy);
            metallicMapButton.Click += (s, e) => { currentTextureMap = 4; if (currentModel != null) currentModel.SetActiveTexture(4); UpdateTextureButtons(); UpdateNoTextureLabel(); glControl.Invalidate(); };
            
            opacityMapButton = AddTextureButton(texGroup, "Opacity", ref gy);
            opacityMapButton.Click += (s, e) => { currentTextureMap = 5; if (currentModel != null) currentModel.SetActiveTexture(5); UpdateTextureButtons(); UpdateNoTextureLabel(); glControl.Invalidate(); };
            
            y += 200;
            showGridButton = AddStyledButton(statsShadingTab, "Show Grid", ref y);
            showGridButton.Click += (s, e) => { showGrid = !showGrid; showGridButton.BackColor = showGrid ? buttonPress : buttonIdle; glControl.Invalidate(); };
            showAxesButton = AddStyledButton(statsShadingTab, "Show Axes", ref y);
            showAxesButton.Click += (s, e) => { showAxes = !showAxes; showAxesButton.BackColor = showAxes ? buttonPress : buttonIdle; glControl.Invalidate(); };
            
            showUVButton = AddStyledButton(statsShadingTab, "Show UV Preview", ref y);
            showUVButton.Click += (s, e) => 
            { 
                showUVPreview = !showUVPreview; 
                previewPanel.Visible = showUVPreview;
                showUVButton.BackColor = showUVPreview ? buttonPress : buttonIdle;
                if (showUVPreview) previewPanel.Invalidate();
            };
            
            AddStyledButton(statsShadingTab, "Performance Mode", ref y);
            y += 20;
            
            var statsLabel = new Label
            {
                Text = "Model Statistics",
                Font = new Font("Segoe UI", 11, FontStyle.Bold),
                Location = new Point(10, y),
                AutoSize = true,
                BackColor = panelBg
            };
            statsShadingTab.Controls.Add(statsLabel);
            y += 30;
            
            verticesLabel = CreateLabel("Vertices: 8", 10, ref y);
            trianglesLabel = CreateLabel("Triangles: 12", 10, ref y);
            edgesLabel = CreateLabel("Edges: 18", 10, ref y);
            
            statsShadingTab.Controls.Add(verticesLabel);
            statsShadingTab.Controls.Add(trianglesLabel);
            statsShadingTab.Controls.Add(edgesLabel);
            
            UpdateTextureButtonStates();
        }
        
        private Button AddStyledButton(Control parent, string text, ref int y)
        {
            var btn = new Button
            {
                Text = text,
                Location = new Point(10, y),
                Size = new Size(270, 28),
                FlatStyle = FlatStyle.Flat,
                BackColor = buttonIdle,
                FlatAppearance = { BorderColor = buttonBorder }
            };
            btn.MouseEnter += (s, e) => { if (btn.Enabled) { btn.BackColor = buttonHover; btn.FlatAppearance.BorderColor = buttonBorderActive; } };
            btn.MouseLeave += (s, e) => { if (btn.Enabled) { btn.BackColor = btn.BackColor == buttonPress ? buttonPress : buttonIdle; btn.FlatAppearance.BorderColor = buttonBorder; } };
            btn.MouseDown += (s, e) => { if (btn.Enabled) btn.BackColor = buttonPress; };
            btn.MouseUp += (s, e) => { if (btn.Enabled) btn.BackColor = buttonHover; };
            parent.Controls.Add(btn);
            y += 32;
            return btn;
        }
        
        private Button AddTextureButton(Control parent, string text, ref int y)
        {
            var btn = new Button
            {
                Text = text,
                Location = new Point(10, y),
                Size = new Size(250, 22),
                FlatStyle = FlatStyle.Flat,
                BackColor = buttonIdle,
                FlatAppearance = { BorderColor = buttonBorder },
                Enabled = true
            };
            btn.MouseEnter += (s, e) => { if (btn.Enabled) { btn.BackColor = buttonHover; btn.FlatAppearance.BorderColor = buttonBorderActive; } };
            btn.MouseLeave += (s, e) => { if (btn.Enabled) { btn.BackColor = btn.BackColor == buttonPress ? buttonPress : buttonIdle; btn.FlatAppearance.BorderColor = buttonBorder; } };
            btn.MouseDown += (s, e) => { if (btn.Enabled) btn.BackColor = buttonPress; };
            btn.MouseUp += (s, e) => { if (btn.Enabled) btn.BackColor = buttonHover; };
            parent.Controls.Add(btn);
            y += 26;
            return btn;
        }
        
        private Label CreateLabel(string text, int x, ref int y)
        {
            var lbl = new Label { Text = text, Location = new Point(x, y), AutoSize = true, BackColor = panelBg };
            y += 22;
            return lbl;
        }
        
        private void UpdateNoTextureLabel()
        {
            if (currentModel == null)
            {
                noTextureLabel.Visible = false;
                return;
            }
            
            bool hasCurrentMap = false;
            switch (currentTextureMap)
            {
                case 0: hasCurrentMap = currentModel.ColorMapId >= 0; break;
                case 1: hasCurrentMap = currentModel.NormalMapId >= 0; break;
                case 2: hasCurrentMap = currentModel.SpecularMapId >= 0; break;
                case 3: hasCurrentMap = currentModel.RoughnessMapId >= 0; break;
                case 4: hasCurrentMap = currentModel.MetallicMapId >= 0; break;
                case 5: hasCurrentMap = currentModel.OpacityMapId >= 0; break;
            }
            
            noTextureLabel.Visible = shadingMode == 2 && !hasCurrentMap;
        }
        
        private void UpdateTextureButtons()
        {
            colorMapButton.BackColor = currentTextureMap == 0 ? buttonPress : buttonIdle;
            normalMapButton.BackColor = currentTextureMap == 1 ? buttonPress : buttonIdle;
            specularMapButton.BackColor = currentTextureMap == 2 ? buttonPress : buttonIdle;
            roughnessMapButton.BackColor = currentTextureMap == 3 ? buttonPress : buttonIdle;
            metallicMapButton.BackColor = currentTextureMap == 4 ? buttonPress : buttonIdle;
            opacityMapButton.BackColor = currentTextureMap == 5 ? buttonPress : buttonIdle;
        }
        
        private void UpdateTextureButtonStates()
        {
            bool enableTexButtons = shadingMode == 2 && currentModel != null;
            colorMapButton.Enabled = enableTexButtons;
            normalMapButton.Enabled = enableTexButtons;
            specularMapButton.Enabled = enableTexButtons;
            roughnessMapButton.Enabled = enableTexButtons;
            metallicMapButton.Enabled = enableTexButtons;
            opacityMapButton.Enabled = enableTexButtons;
            
            if (!enableTexButtons)
            {
                colorMapButton.BackColor = Color.FromArgb(230, 230, 230);
                normalMapButton.BackColor = Color.FromArgb(230, 230, 230);
                specularMapButton.BackColor = Color.FromArgb(230, 230, 230);
                roughnessMapButton.BackColor = Color.FromArgb(230, 230, 230);
                metallicMapButton.BackColor = Color.FromArgb(230, 230, 230);
                opacityMapButton.BackColor = Color.FromArgb(230, 230, 230);
            }
            else
            {
                UpdateTextureButtons();
            }
        }
        
        private void UpdateShadingButtons()
        {
            solidButton.BackColor = shadingMode == 0 ? buttonPress : buttonIdle;
            wireframeButton.BackColor = shadingMode == 1 ? buttonPress : buttonIdle;
            textureButton.BackColor = shadingMode == 2 ? buttonPress : buttonIdle;
        }
        
        private void LightControlPanel_Paint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            
            int centerX = lightControlPanel.Width / 2;
            int centerY = lightControlPanel.Height / 2;
            int radius = 70;
            
            g.DrawEllipse(Pens.Gray, centerX - radius, centerY - radius, radius * 2, radius * 2);
            
            int lightX = (int)(centerX + Math.Cos(lightAngle) * (radius - 10));
            int lightY = (int)(centerY - Math.Sin(lightAngle) * (radius - 10));
            
            g.DrawLine(new Pen(Color.FromArgb(0, 120, 215), 2), centerX, centerY, lightX, lightY);
            g.FillEllipse(Brushes.Yellow, lightX - 8, lightY - 8, 16, 16);
        }
        
        private void LightControlPanel_MouseDown(object sender, MouseEventArgs e)
        {
            draggingLight = true;
            UpdateLightAngle(e.Location);
        }
        
        private void LightControlPanel_MouseUp(object sender, MouseEventArgs e)
        {
            draggingLight = false;
        }
        
        private void LightControlPanel_MouseMove(object sender, MouseEventArgs e)
        {
            if (draggingLight)
                UpdateLightAngle(e.Location);
        }
        
        private void UpdateLightAngle(Point mousePos)
        {
            int centerX = lightControlPanel.Width / 2;
            int centerY = lightControlPanel.Height / 2;
            float dx = mousePos.X - centerX;
            float dy = centerY - mousePos.Y;
            lightAngle = (float)Math.Atan2(dy, dx);
            lightControlPanel.Invalidate();
            glControl.Invalidate();
        }
        
        private void PreviewPanel_Paint(object sender, PaintEventArgs e)
        {
            if (!showUVPreview || currentModel == null || currentModel.TexCoords.Count == 0)
                return;
                
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.FromArgb(45, 45, 45));
            
            float padding = 10f;
            float scale = Math.Min(previewPanel.Width - padding * 2, previewPanel.Height - padding * 2);
            
            // Draw grid lines
            using (var gridPen = new Pen(Color.FromArgb(80, 80, 80), 0.5f))
            {
                for (int i = 0; i <= 4; i++)
                {
                    float pos = padding + (i / 4f) * scale;
                    g.DrawLine(gridPen, pos, padding, pos, padding + scale);
                    g.DrawLine(gridPen, padding, pos, padding + scale, pos);
                }
            }
            
            // UV boundary
            g.DrawRectangle(new Pen(Color.FromArgb(120, 120, 120), 1f), padding, padding, scale, scale);
            
            // Draw all UV edges with deduplication
            using (var pen = new Pen(Color.FromArgb(255, 165, 0), 1.0f))
            {
                var drawnEdges = new HashSet<string>();
                foreach (var face in currentModel.Faces)
                {
                    for (int i = 0; i < face.TexCoordIndices.Length; i++)
                    {
                        int idx1 = face.TexCoordIndices[i];
                        int idx2 = face.TexCoordIndices[(i + 1) % face.TexCoordIndices.Length];
                        
                        if (idx1 >= 0 && idx1 < currentModel.TexCoords.Count &&
                            idx2 >= 0 && idx2 < currentModel.TexCoords.Count)
                        {
                            string edgeKey = Math.Min(idx1, idx2) + "_" + Math.Max(idx1, idx2);
                            if (drawnEdges.Add(edgeKey))
                            {
                                var uv1 = currentModel.TexCoords[idx1];
                                var uv2 = currentModel.TexCoords[idx2];
                                float x1 = padding + uv1.X * scale;
                                float y1 = padding + (1.0f - uv1.Y) * scale;
                                float x2 = padding + uv2.X * scale;
                                float y2 = padding + (1.0f - uv2.Y) * scale;
                                g.DrawLine(pen, x1, y1, x2, y2);
                            }
                        }
                    }
                }
            }
        }
        
        private void SetupGL()
        {
            glControl.MakeCurrent();
            
            mainShader = new Shader(ShaderSource.VertexShader, ShaderSource.FragmentShader);
            wireframeShader = new Shader(ShaderSource.WireframeVertexShader, ShaderSource.WireframeFragmentShader);
            
            GL.Enable(EnableCap.DepthTest);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.Enable(EnableCap.LineSmooth);
            GL.Hint(HintTarget.LineSmoothHint, HintMode.Nicest);
            GL.ClearColor(0.96f, 0.96f, 0.96f, 1f);
            
            BuildCubeBuffers();
            SetupProjection();
        }
        
        private void BuildCubeBuffers()
        {
            float[] cubeVertices = {
                -1,-1, 1,  0, 0, 1,  0,0,  1,0,0,
                 1,-1, 1,  0, 0, 1,  1,0,  1,0,0,
                 1, 1, 1,  0, 0, 1,  1,1,  1,0,0,
                -1, 1, 1,  0, 0, 1,  0,1,  1,0,0,
                
                -1,-1,-1,  0, 0,-1,  0,0,  1,0,0,
                -1, 1,-1,  0, 0,-1,  0,1,  1,0,0,
                 1, 1,-1,  0, 0,-1,  1,1,  1,0,0,
                 1,-1,-1,  0, 0,-1,  1,0,  1,0,0,
                
                -1,-1,-1, -1, 0, 0,  0,0,  0,1,0,
                -1,-1, 1, -1, 0, 0,  1,0,  0,1,0,
                -1, 1, 1, -1, 0, 0,  1,1,  0,1,0,
                -1, 1,-1, -1, 0, 0,  0,1,  0,1,0,
                
                 1,-1,-1,  1, 0, 0,  0,0,  0,1,0,
                 1, 1,-1,  1, 0, 0,  0,1,  0,1,0,
                 1, 1, 1,  1, 0, 0,  1,1,  0,1,0,
                 1,-1, 1,  1, 0, 0,  1,0,  0,1,0,
                
                -1, 1,-1,  0, 1, 0,  0,0,  0,0,1,
                -1, 1, 1,  0, 1, 0,  0,1,  0,0,1,
                 1, 1, 1,  0, 1, 0,  1,1,  0,0,1,
                 1, 1,-1,  0, 1, 0,  1,0,  0,0,1,
                
                -1,-1,-1,  0,-1, 0,  0,0,  0,0,1,
                 1,-1,-1,  0,-1, 0,  1,0,  0,0,1,
                 1,-1, 1,  0,-1, 0,  1,1,  0,0,1,
                -1,-1, 1,  0,-1, 0,  0,1,  0,0,1
            };
            
            uint[] cubeIndices = {
                0,1,2, 2,3,0,
                4,5,6, 6,7,4,
                8,9,10, 10,11,8,
                12,13,14, 14,15,12,
                16,17,18, 18,19,16,
                20,21,22, 22,23,20
            };
            
            cubeVAO = GL.GenVertexArray();
            cubeVBO = GL.GenBuffer();
            int cubeEBO = GL.GenBuffer();
            
            GL.BindVertexArray(cubeVAO);
            
            GL.BindBuffer(BufferTarget.ArrayBuffer, cubeVBO);
            GL.BufferData(BufferTarget.ArrayBuffer, cubeVertices.Length * sizeof(float), cubeVertices, BufferUsageHint.StaticDraw);
            
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, cubeEBO);
            GL.BufferData(BufferTarget.ElementArrayBuffer, cubeIndices.Length * sizeof(uint), cubeIndices, BufferUsageHint.StaticDraw);
            
            int stride = 11 * sizeof(float);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));
            GL.EnableVertexAttribArray(2);
            GL.VertexAttribPointer(3, 3, VertexAttribPointerType.Float, false, stride, 8 * sizeof(float));
            GL.EnableVertexAttribArray(3);
            
            GL.BindVertexArray(0);
        }
        
        private void SetupProjection()
        {
            if (glControl.Width == 0 || glControl.Height == 0) return;
            GL.Viewport(0, 0, glControl.Width, glControl.Height);
        }
        
        private void GlControl_Paint(object sender, PaintEventArgs e)
        {
            glControl.MakeCurrent();
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            
            float aspect = (float)glControl.Width / glControl.Height;
            Matrix4 projection = Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(45f), aspect, 0.1f, 1000f);
            
            // NEW CAMERA SYSTEM - Fixed for Blender Z-up compatibility
            float distance = Math.Abs(zoom);
            float radX = MathHelper.DegreesToRadians(rotationX);
            float radY = MathHelper.DegreesToRadians(rotationY);
            
            Vector3 offset = new Vector3(
                distance * (float)(Math.Sin(radY) * Math.Cos(radX)),
                distance * (float)Math.Sin(radX),
                distance * (float)(Math.Cos(radY) * Math.Cos(radX))
            );
            
            Vector3 cameraPos = lookAtPoint + offset;
            Vector3 cameraTarget = lookAtPoint + new Vector3(panX, panY, 0);
            Vector3 cameraUp = new Vector3(0, 1, 0);
            
            Matrix4 view = Matrix4.LookAt(cameraPos, cameraTarget, cameraUp);
            Vector3 viewPos = cameraPos;
            
            // Light positions - lightAngle is already in radians from Math.Atan2
            float lightDist = Math.Abs(zoom) * 2.5f + 5f; // scale with scene
            Vector3 lightPos = new Vector3(
                lightDist * (float)Math.Cos(lightAngle),
                lightDist * 0.6f,
                lightDist * (float)Math.Sin(lightAngle)
            );
            
            Vector3 lightPos2 = new Vector3(
                lightDist * (float)Math.Cos(lightAngle + Math.PI),
                lightDist * 0.4f,
                lightDist * (float)Math.Sin(lightAngle + Math.PI)
            );
            
            if (shadingMode == 1)
            {
                wireframeShader.Use();
                wireframeShader.SetMat4("projection", ref projection);
                wireframeShader.SetMat4("view", ref view);
                
                Matrix4 model = Matrix4.Identity;
                if (!showDefaultCube && currentModel != null)
                {
                    model = Matrix4.CreateTranslation(-cachedModelCenter);
                }
                wireframeShader.SetMat4("model", ref model);
                wireframeShader.SetVec3("color", new Vector3(0.96f, 0.96f, 0.96f));
                
                if (showDefaultCube)
                {
                    GL.BindVertexArray(cubeVAO);
                    GL.DrawElements(PrimitiveType.Triangles, 36, DrawElementsType.UnsignedInt, 0);
                }
                else if (currentModel != null)
                {
                    GL.BindVertexArray(currentModel.VAO);
                    GL.DrawElements(PrimitiveType.Triangles, currentModel.Faces.Count * 3, DrawElementsType.UnsignedInt, 0);
                }
                
                GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Line);
                GL.LineWidth(1.0f);
                wireframeShader.SetVec3("color", new Vector3(0f, 0f, 0f));
                
                if (showDefaultCube)
                {
                    GL.BindVertexArray(cubeVAO);
                    GL.DrawElements(PrimitiveType.Triangles, 36, DrawElementsType.UnsignedInt, 0);
                }
                else if (currentModel != null)
                {
                    GL.BindVertexArray(currentModel.VAO);
                    GL.DrawElements(PrimitiveType.Triangles, currentModel.Faces.Count * 3, DrawElementsType.UnsignedInt, 0);
                }
                
                GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
            }
            else
            {
                mainShader.Use();
                mainShader.SetVec3("lightPos", lightPos);
                mainShader.SetVec3("lightPos2", lightPos2);
                mainShader.SetVec3("viewPos", viewPos);
                mainShader.SetMat4("projection", ref projection);
                mainShader.SetMat4("view", ref view);
                
                Matrix4 model = Matrix4.Identity;
                if (!showDefaultCube && currentModel != null)
                {
                    model = Matrix4.CreateTranslation(-cachedModelCenter);
                }
                
                mainShader.SetMat4("model", ref model);
                
                if (showDefaultCube)
                {
                    mainShader.SetInt("shadingMode", 0);
                    mainShader.SetVec3("solidColor", new Vector3(0.5f, 0.7f, 1f));
                    mainShader.SetInt("hasColorMap", 0);
                    mainShader.SetInt("hasNormalMap", 0);
                    mainShader.SetInt("hasSpecularMap", 0);
                    mainShader.SetInt("hasRoughnessMap", 0);
                    mainShader.SetInt("hasMetallicMap", 0);
                    mainShader.SetInt("hasOpacityMap", 0);
                    
                    GL.BindVertexArray(cubeVAO);
                    GL.DrawElements(PrimitiveType.Triangles, 36, DrawElementsType.UnsignedInt, 0);
                }
                else if (currentModel != null)
                {
                    currentModel.Render(mainShader, shadingMode);
                }
            }
            
            GL.BindVertexArray(0);
            
            // Draw grid and/or axes using immediate-mode lines via wireframeShader
            if (showGrid || showAxes)
            {
                wireframeShader.Use();
                wireframeShader.SetMat4("projection", ref projection);
                wireframeShader.SetMat4("view", ref view);
                Matrix4 identModel = Matrix4.Identity;
                wireframeShader.SetMat4("model", ref identModel);
                GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
                
                // Build grid/axes geometry into a temporary VBO
                var lineVerts = new System.Collections.Generic.List<float>();
                
                if (showGrid)
                {
                    float gridSize = Math.Max(cachedModelSize, 2f) * 2f;
                    float step = gridSize / 10f;
                    float yLevel = !showDefaultCube && currentModel != null ? cachedModelCenter.Y - cachedModelSize * 0.5f : -1f;
                    for (int gi = -10; gi <= 10; gi++)
                    {
                        // Lines along Z
                        lineVerts.AddRange(new float[]{ gi * step, yLevel, -gridSize,  gi * step, yLevel, gridSize });
                        // Lines along X
                        lineVerts.AddRange(new float[]{ -gridSize, yLevel, gi * step,  gridSize, yLevel, gi * step });
                    }
                }
                
                if (showAxes)
                {
                    float axLen = Math.Max(cachedModelSize, 2f);
                    Vector3 orig = !showDefaultCube && currentModel != null ? cachedModelCenter : Vector3.Zero;
                    // X axis
                    lineVerts.AddRange(new float[]{ orig.X, orig.Y, orig.Z,  orig.X + axLen, orig.Y, orig.Z });
                    // Y axis
                    lineVerts.AddRange(new float[]{ orig.X, orig.Y, orig.Z,  orig.X, orig.Y + axLen, orig.Z });
                    // Z axis
                    lineVerts.AddRange(new float[]{ orig.X, orig.Y, orig.Z,  orig.X, orig.Y, orig.Z + axLen });
                }
                
                float[] verts = lineVerts.ToArray();
                int tmpVAO = GL.GenVertexArray();
                int tmpVBO = GL.GenBuffer();
                GL.BindVertexArray(tmpVAO);
                GL.BindBuffer(BufferTarget.ArrayBuffer, tmpVBO);
                GL.BufferData(BufferTarget.ArrayBuffer, verts.Length * sizeof(float), verts, BufferUsageHint.StreamDraw);
                GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
                GL.EnableVertexAttribArray(0);
                
                GL.LineWidth(1.0f);
                int lineCount = 0;
                
                if (showGrid)
                {
                    wireframeShader.SetVec3("color", new Vector3(0.7f, 0.7f, 0.7f));
                    int gridLines = 21 * 2 * 2; // 21 lines each direction, 2 verts each
                    GL.DrawArrays(PrimitiveType.Lines, 0, gridLines);
                    lineCount = gridLines;
                }
                
                if (showAxes)
                {
                    GL.LineWidth(2.5f);
                    // X - red
                    wireframeShader.SetVec3("color", new Vector3(0.9f, 0.15f, 0.15f));
                    GL.DrawArrays(PrimitiveType.Lines, lineCount, 2);
                    // Y - green
                    wireframeShader.SetVec3("color", new Vector3(0.15f, 0.8f, 0.15f));
                    GL.DrawArrays(PrimitiveType.Lines, lineCount + 2, 2);
                    // Z - blue
                    wireframeShader.SetVec3("color", new Vector3(0.15f, 0.4f, 0.9f));
                    GL.DrawArrays(PrimitiveType.Lines, lineCount + 4, 2);
                    GL.LineWidth(1.0f);
                }
                
                GL.BindVertexArray(0);
                GL.DeleteVertexArray(tmpVAO);
                GL.DeleteBuffer(tmpVBO);
            }
            
            glControl.SwapBuffers();
        }
        private void RenderUVPreview()
        {
            if (currentModel == null) return;
            
            using (var bmp = new Bitmap(previewPanel.Width, previewPanel.Height))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                
                // Draw border
                g.DrawRectangle(Pens.LightGray, 0, 0, previewPanel.Width - 1, previewPanel.Height - 1);
                
                float padding = 10;
                float scale = Math.Min(previewPanel.Width - padding * 2, previewPanel.Height - padding * 2);
                
                // Draw UV grid
                using (var gridPen = new Pen(Color.FromArgb(200, 200, 200), 0.5f))
                {
                    for (int i = 0; i <= 10; i++)
                    {
                        float pos = padding + (i / 10f) * scale;
                        g.DrawLine(gridPen, pos, padding, pos, padding + scale);
                        g.DrawLine(gridPen, padding, pos, padding + scale, pos);
                    }
                }
                
                // Draw UV boundary
                g.DrawRectangle(Pens.Black, padding, padding, scale, scale);
                
                // Draw all UV edges
                using (var pen = new Pen(Color.Blue, 1.5f))
                {
                    var drawnEdges = new HashSet<string>();
                    
                    foreach (var face in currentModel.Faces)
                    {
                        for (int i = 0; i < face.TexCoordIndices.Length; i++)
                        {
                            int idx1 = face.TexCoordIndices[i];
                            int idx2 = face.TexCoordIndices[(i + 1) % face.TexCoordIndices.Length];
                            
                            if (idx1 >= 0 && idx1 < currentModel.TexCoords.Count &&
                                idx2 >= 0 && idx2 < currentModel.TexCoords.Count)
                            {
                                string edgeKey = Math.Min(idx1, idx2) + "_" + Math.Max(idx1, idx2);
                                
                                if (!drawnEdges.Contains(edgeKey))
                                {
                                    drawnEdges.Add(edgeKey);
                                    
                                    var uv1 = currentModel.TexCoords[idx1];
                                    var uv2 = currentModel.TexCoords[idx2];
                                    
                                    float x1 = padding + uv1.X * scale;
                                    float y1 = padding + (1.0f - uv1.Y) * scale;
                                    float x2 = padding + uv2.X * scale;
                                    float y2 = padding + (1.0f - uv2.Y) * scale;
                                    
                                    g.DrawLine(pen, x1, y1, x2, y2);
                                }
                            }
                        }
                    }
                }
                
                previewPanel.BackgroundImage?.Dispose();
                previewPanel.BackgroundImage = bmp.Clone() as Bitmap;
            }
        }

        private void LoadButton_Click(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter = "OBJ files (*.obj)|*.obj|All files (*.*)|*.*";
                if (dialog.ShowDialog() == DialogResult.OK)
                    LoadOBJFile(dialog.FileName);
            }
        }
        
        private void LoadOBJFile(string filename)
        {
            if (currentModel != null)
                currentModel.Cleanup();
                
            currentModel = new OBJLoader();
            currentModel.LoadFromFile(filename);
            showDefaultCube = false;
            
            // Default to textured mode
            shadingMode = 2;
            currentTextureMap = 0;
            UpdateShadingButtons();
            
            var modelSize = currentModel.GetModelSize();
            cachedModelSize = modelSize;
            cachedModelCenter = currentModel.GetModelCenter();
            zoom = -modelSize * 1.8f;
            lookAtPoint = cachedModelCenter;
            
            UpdateLabels();
            UpdateTextureButtonStates();
            if (showUVPreview) previewPanel.Invalidate();
            glControl.Invalidate();
        }
        private void UpdateLabels()
        {
            if (currentModel != null)
            {
                verticesLabel.Text = $"Vertices: {currentModel.Vertices.Count}";
                trianglesLabel.Text = $"Triangles: {currentModel.Faces.Count}";
                edgesLabel.Text = $"Edges: {currentModel.EdgeCount}";
            }
            else
            {
                verticesLabel.Text = "Vertices: 8";
                trianglesLabel.Text = "Triangles: 12";
                edgesLabel.Text = "Edges: 18";
            }
        }

        private void ExportMTL(string objFilePath)
        {
            if (currentModel == null) return;
            
            string mtlPath = Path.ChangeExtension(objFilePath, ".mtl");
            string materialName = Path.GetFileNameWithoutExtension(objFilePath);
            
            using (var writer = new StreamWriter(mtlPath))
            {
                writer.WriteLine("# Blender 3.6.23");
                writer.WriteLine("# www.blender.org");
                writer.WriteLine($"newmtl {materialName}");
                writer.WriteLine("Ns 250.000000");
                writer.WriteLine("Ka 1.000000 1.000000 1.000000");
                writer.WriteLine("Ke 0.000000 0.000000 0.000000");
                writer.WriteLine("Ni 1.450000");
                writer.WriteLine("d 1.000000");
                writer.WriteLine("illum 2");
                
                if (currentModel.ColorMapId >= 0)
                {
                    writer.WriteLine($"map_Kd {materialName}_color.png");
                }
                
                if (currentModel.SpecularMapId >= 0)
                {
                    writer.WriteLine($"map_Ks {materialName}_specular.png");
                }
                
                if (currentModel.NormalMapId >= 0)
                {
                    writer.WriteLine($"map_Bump -bm 1.000000 {materialName}_normal.png");
                }
            }
            
            MessageBox.Show($"MTL file exported to:\n{mtlPath}", "Export Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        
        private void GlControl_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) 
            {
                TimeSpan timeSinceLastClick = DateTime.Now - lastClickTime;
                if (timeSinceLastClick.TotalMilliseconds < 100)
                {
                    ResetCamera();
                }
                
                draggingRotate = true; 
                lastMousePos = e.Location;
                lastClickTime = DateTime.Now;
            }
            else if (e.Button == MouseButtons.Right) 
            { 
                draggingPan = true; 
                lastMousePos = e.Location; 
            }
            else if (e.Button == MouseButtons.Middle) 
            { 
              draggingPan = true; 
              lastMousePos = e.Location; 
            }
        }

        private void GlControl_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) draggingRotate = false;
            else if (e.Button == MouseButtons.Right) draggingPan = false;
            else if (e.Button == MouseButtons.Middle) draggingPan = false;
        }
        
        private void GlControl_MouseMove(object sender, MouseEventArgs e)
        {
            if (draggingRotate)
            {
                rotationY += (e.X - lastMousePos.X) * 0.5f;
                rotationX += (e.Y - lastMousePos.Y) * 0.5f;
                rotationX = Math.Max(minRotationX, Math.Min(maxRotationX, rotationX));
                lastMousePos = e.Location;
                glControl.Invalidate();
            }
            else if (draggingPan)
            {
                float panScale = Math.Abs(zoom) * 0.002f;
                float dx = (e.X - lastMousePos.X) * panScale;
                float dy = (e.Y - lastMousePos.Y) * panScale;
                
                float radY = MathHelper.DegreesToRadians(rotationY);
                float radX = MathHelper.DegreesToRadians(rotationX);
                
                Vector3 right = new Vector3((float)Math.Cos(radY), 0, -(float)Math.Sin(radY));
                Vector3 up = new Vector3(
                    (float)(Math.Sin(radY) * Math.Sin(radX)),
                    (float)Math.Cos(radX),
                    (float)(Math.Cos(radY) * Math.Sin(radX))
                );
                
                lookAtPoint += right * dx + up * dy;
                
                lastMousePos = e.Location;
                glControl.Invalidate();
            }
        }
        private void GlControl_MouseWheel(object sender, MouseEventArgs e)
        {
            float scaleFactor = (e.Delta > 0) ? 0.9f : 1.1f;
            zoom *= scaleFactor;
            zoom = Math.Max(maxZoom, Math.Min(minZoom, zoom));
            glControl.Invalidate();
        }
        
        private void GlControl_MouseDown_MiddleButton(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Middle)
            {
                draggingPan = true;
                lastMousePos = e.Location;
            }
        }
        private void Viewer3DForm_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effect = DragDropEffects.Copy;
        }
        
        private void Viewer3DForm_DragDrop(object sender, DragEventArgs e)
        {
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files.Length > 0)
            {
                string file = files[0];
                if (file.EndsWith(".obj", StringComparison.OrdinalIgnoreCase))
                {
                    LoadOBJFile(file);
                }
                else if (currentModel != null && (
                    file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)))
                {
                    currentModel.LoadTexture(file, currentTextureMap);
                    UpdateTextureButtonStates();
                    UpdateNoTextureLabel();
                    glControl.Invalidate();
                }
            }
        }
        
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (currentModel != null)
                currentModel.Cleanup();
            GL.DeleteVertexArray(cubeVAO);
            GL.DeleteBuffer(cubeVBO);
        }
    }
    
    static class Program
    {
        [STAThread]
        static void Main() 
        { 
            Application.EnableVisualStyles(); 
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Viewer3DForm()); 
        }
    }
}