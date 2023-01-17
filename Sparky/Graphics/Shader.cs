using System.Runtime.InteropServices;
using System.Text;
using Spirzza.Interop.Shaderc;

namespace Sparky.Graphics;

public class Shader
{
    private string sourceText;
    private ShaderType shaderType;
    private byte[] spirvOutput;

    public int Length => spirvOutput.Length;
    public byte[] Bytecode => spirvOutput;
    
    private Shader(string sourceText, ShaderType shaderType)
    {
        this.sourceText = sourceText;
        this.shaderType = shaderType;
    }

    public void Compile()
    {
        Debug.Log("Compiling shader...");

        unsafe
        {
            shaderc_compiler* compiler = Shaderc.shaderc_compiler_initialize();
            shaderc_compilation_result* result;

            shaderc_shader_kind kind = this.shaderType switch
            {
                ShaderType.VertexShader => shaderc_shader_kind.shaderc_glsl_vertex_shader,
                ShaderType.FragmentShader => shaderc_shader_kind.shaderc_glsl_fragment_shader
            };
            
            fixed(sbyte* src = GetFromString(sourceText))
            fixed(sbyte* fn = GetFromString("main"))
            fixed (sbyte* entryPoint = GetFromString("main"))
            {
                result = Shaderc.shaderc_compile_into_spv(compiler, src, (nuint) sourceText.Length,
                    kind, fn, entryPoint, null);
            }
            
            try
            {
                if (Shaderc.shaderc_result_get_compilation_status(result) !=
                    shaderc_compilation_status.shaderc_compilation_status_success)
                {
                    string errorMessage = $"Failed to convert shader: " +
                                          ConvertToString(Shaderc.shaderc_result_get_error_message(result));
                    throw new SparkyException(errorMessage);
                }

                int length = (int) Shaderc.shaderc_result_get_length(result);
                this.spirvOutput = new byte[length];


                IntPtr srcBytes = (IntPtr)Shaderc.shaderc_result_get_bytes(result);
                Marshal.Copy(srcBytes, this.spirvOutput, 0, this.spirvOutput.Length);
            }
            finally
            {
                if (result != null)
                    Shaderc.shaderc_result_release(result);
                Shaderc.shaderc_compiler_release(compiler);
            }

            
        }
    }

    private unsafe string ConvertToString(sbyte* ptr)
    {
        return Marshal.PtrToStringAnsi((IntPtr) ptr) ?? string.Empty;
    }
    
    private sbyte[] GetFromString(string str)
    {
        return (sbyte[])(Array)Encoding.ASCII.GetBytes(str);
    }
    
    internal static Shader FromStream(Stream stream, ShaderType shaderType)
    {
        using StreamReader streamReader = new StreamReader(stream, leaveOpen: true);
        string text = streamReader.ReadToEnd();
        return new Shader(text, shaderType);
    }

    internal static Shader FromFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(path);

        string extension = Path.GetExtension(path);
        ShaderType shaderType = extension.ToLower() switch
        {
            "vert" => ShaderType.VertexShader,
            "frag" => ShaderType.FragmentShader,
            _ => throw new SparkyException($"Could not detect shader type for " + path)
        };
        
        using Stream stream = File.OpenRead(path);
        return FromStream(stream, shaderType);
    }
}