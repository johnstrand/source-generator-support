using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using SdkCollector.Models;

namespace SdkCollector.Services;

internal static class PeVersionReader
{
    public static AssemblyVersionDetails ReadFromBytes(ReadOnlySpan<byte> peBytes, string displayName)
    {
        if (peBytes.IsEmpty)
        {
            throw new InvalidDataException($"PE payload for '{displayName}' is empty.");
        }

        ImmutableArray<byte> immutableBytes = ImmutableCollectionsMarshal.AsImmutableArray(peBytes.ToArray());
        using var peReader = new PEReader(immutableBytes);
        if (!peReader.HasMetadata)
        {
            throw new InvalidDataException($"PE payload for '{displayName}' does not contain .NET metadata.");
        }

        MetadataReader metadataReader = peReader.GetMetadataReader();
        AssemblyDefinition assemblyDef = metadataReader.GetAssemblyDefinition();
        string assemblyVersion = FormatFourPartVersion(assemblyDef.Version);

        string? fileVersion = null;
        string? informationalVersion = null;

        foreach (CustomAttributeHandle attrHandle in assemblyDef.GetCustomAttributes())
        {
            CustomAttribute attr = metadataReader.GetCustomAttribute(attrHandle);
            string? fullTypeName = TryGetAttributeFullName(metadataReader, attr.Constructor);
            if (fullTypeName is null)
            {
                continue;
            }

            if (fullTypeName == "System.Reflection.AssemblyFileVersionAttribute")
            {
                fileVersion ??= TryReadSingleStringArgument(metadataReader, attr.Value);
            }
            else if (fullTypeName == "System.Reflection.AssemblyInformationalVersionAttribute")
            {
                informationalVersion ??= TryReadSingleStringArgument(metadataReader, attr.Value);
            }
        }

        if (string.IsNullOrWhiteSpace(fileVersion) || string.IsNullOrWhiteSpace(informationalVersion))
        {
            string tempFile = Path.Combine(Path.GetTempPath(), $"pe_{Guid.NewGuid():N}.dll");
            try
            {
                File.WriteAllBytes(tempFile, peBytes.ToArray());
                FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(tempFile);
                if (string.IsNullOrWhiteSpace(fileVersion) && !string.IsNullOrWhiteSpace(fvi.FileVersion))
                {
                    fileVersion = fvi.FileVersion;
                }
                if (string.IsNullOrWhiteSpace(informationalVersion) && !string.IsNullOrWhiteSpace(fvi.ProductVersion))
                {
                    informationalVersion = fvi.ProductVersion;
                }
            }
            finally
            {
                try { File.Delete(tempFile); } catch { /* ignore cleanup error */ }
            }
        }

        string? commitSha = ExtractCommitSha(informationalVersion);

        return new AssemblyVersionDetails
        {
            AssemblyVersion = assemblyVersion,
            FileVersion = string.IsNullOrWhiteSpace(fileVersion) ? null : fileVersion.Trim(),
            InformationalVersion = string.IsNullOrWhiteSpace(informationalVersion) ? null : informationalVersion.Trim(),
            CommitSha = commitSha
        };
    }

    private static string FormatFourPartVersion(Version version)
    {
        int major = Math.Max(0, version.Major);
        int minor = Math.Max(0, version.Minor);
        int build = Math.Max(0, version.Build);
        int revision = Math.Max(0, version.Revision);
        return $"{major}.{minor}.{build}.{revision}";
    }

    private static string? TryGetAttributeFullName(MetadataReader reader, EntityHandle constructor)
    {
        if (constructor.Kind == HandleKind.MemberReference)
        {
            MemberReference memberRef = reader.GetMemberReference((MemberReferenceHandle)constructor);
            EntityHandle parent = memberRef.Parent;
            if (parent.Kind == HandleKind.TypeReference)
            {
                TypeReference typeRef = reader.GetTypeReference((TypeReferenceHandle)parent);
                string ns = reader.GetString(typeRef.Namespace);
                string name = reader.GetString(typeRef.Name);
                return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
            }
            if (parent.Kind == HandleKind.TypeDefinition)
            {
                TypeDefinition typeDef = reader.GetTypeDefinition((TypeDefinitionHandle)parent);
                string ns = reader.GetString(typeDef.Namespace);
                string name = reader.GetString(typeDef.Name);
                return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
            }
        }
        else if (constructor.Kind == HandleKind.MethodDefinition)
        {
            MethodDefinition methodDef = reader.GetMethodDefinition((MethodDefinitionHandle)constructor);
            TypeDefinitionHandle declaringTypeHandle = methodDef.GetDeclaringType();
            if (!declaringTypeHandle.IsNil)
            {
                TypeDefinition typeDef = reader.GetTypeDefinition(declaringTypeHandle);
                string ns = reader.GetString(typeDef.Namespace);
                string name = reader.GetString(typeDef.Name);
                return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
            }
        }

        return null;
    }

    private static string? TryReadSingleStringArgument(MetadataReader reader, BlobHandle valueBlob)
    {
        if (valueBlob.IsNil)
        {
            return null;
        }

        BlobReader blob = reader.GetBlobReader(valueBlob);
        if (blob.Length < 4)
        {
            return null;
        }

        ushort prolog = blob.ReadUInt16();
        if (prolog != 0x0001)
        {
            return null;
        }

        return blob.ReadSerializedString();
    }

    internal static string? ExtractCommitSha(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return null;
        }

        int plusIdx = informationalVersion.IndexOf('+');
        if (plusIdx < 0 || plusIdx == informationalVersion.Length - 1)
        {
            return null;
        }

        string candidate = informationalVersion[(plusIdx + 1)..].Trim();
        int dotOrSpace = candidate.IndexOfAny([' ', ';', ',']);
        if (dotOrSpace > 0)
        {
            candidate = candidate[..dotOrSpace];
        }

        if (candidate.Length is >= 7 and <= 40 && candidate.All(IsHexChar))
        {
            return candidate;
        }

        return null;
    }

    private static bool IsHexChar(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
}
