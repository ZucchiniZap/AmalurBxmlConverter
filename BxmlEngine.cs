using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace AmalurBxmlConverter;

public class BxmlEngine
{
    public bool IsDatabaseLoaded { get; private set; }
    public int LoadedSymbolCount => _symbols.Count;
    
    private readonly SymbolManager _symbols = new();
    private readonly AmalurBxmlFormatHandler _handler = new();

    public void LoadGlobalDictionaries(string directoryPath)
    {
        _symbols.LoadCsvDirectory(directoryPath);
        IsDatabaseLoaded = true;
    }

    public XDocument Decompile(string binaryPath)
    {
        using var fs = File.OpenRead(binaryPath);
        using var reader = new BinaryReader(fs, Encoding.UTF8);
        return _handler.Read(reader, _symbols);
    }

    public void Compile(string xmlPath, string outBinaryPath)
    {
        var xml = XDocument.Load(xmlPath);
        using var fs = File.Create(outBinaryPath);
        using var writer = new BinaryWriter(fs, Encoding.UTF8);
        _handler.Write(writer, xml, _symbols);
    }
}

public class SymbolManager
{
    private readonly Dictionary<uint, string> _idToString = new();
    private readonly Dictionary<string, uint> _stringToId = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _idToString.Count;

    public void LoadCsvDirectory(string directoryPath)
    {
        var files = Directory.GetFiles(directoryPath, "*.csv", SearchOption.AllDirectories);

        foreach (var file in files)
        {
            var lines = File.ReadLines(file);
            bool isFirstLine = true;

            foreach (var line in lines) 
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                // Only skip the first line IF it is actually a header text
                if (isFirstLine)
                {
                    isFirstLine = false;
                    if (line.StartsWith("id", StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                int commaIdx = line.IndexOf(',');
                if (commaIdx > 0 && commaIdx < line.Length - 1)
                {
                    string idStr = line.Substring(0, commaIdx).Trim();
                    string valStr = line.Substring(commaIdx + 1).Trim();

                    if (valStr.StartsWith("\"") && valStr.EndsWith("\"") && valStr.Length >= 2)
                        valStr = valStr.Substring(1, valStr.Length - 2);

                    if (uint.TryParse(idStr, out uint id))
                    {
                        _idToString[id] = valStr;
                        _stringToId[valStr] = id;
                    }
                }
            }
        }
    }

    public string GetSymbolName(uint id) => 
        _idToString.TryGetValue(id, out var name) ? name : $"UNKNOWN_{id}";

    public bool TryGetSymbolId(string name, out uint id) => 
        _stringToId.TryGetValue(name, out id);
}

public class AmalurBxmlFormatHandler
{
    private class BxmlNodeStruct
    {
        public int ParentIndex;
        public int FirstChildIndex;
        public int NextSiblingIndex;
        public int AttributeCount;
        public int AttributeStartIndex;
    }

    private class BxmlAttrStruct
    {
        public int NameSymbolId;
        public int ValueIndex;
        public byte Type;
    }

    public XDocument Read(BinaryReader reader, SymbolManager symbols)
    {
        int nodeCount = reader.ReadInt32();
        var nodes = new BxmlNodeStruct[nodeCount];
        
        for (int i = 0; i < nodeCount; i++)
        {
            nodes[i] = new BxmlNodeStruct
            {
                ParentIndex = reader.ReadInt32(),
                FirstChildIndex = reader.ReadInt32(),
                NextSiblingIndex = reader.ReadInt32(),
                AttributeCount = reader.ReadInt32(),
                AttributeStartIndex = reader.ReadInt32()
            };
        }

        int attrCount = reader.ReadInt32();
        var attrs = new BxmlAttrStruct[attrCount];
        
        for (int i = 0; i < attrCount; i++)
        {
            uint packed = reader.ReadUInt32();
            attrs[i] = new BxmlAttrStruct
            {
                NameSymbolId = (int)(packed & 0xFFF),
                ValueIndex = (int)((packed >> 12) & 0xFFF),
                Type = (byte)((packed >> 24) & 0xFF)
            };
        }

        int symCount = reader.ReadInt32();
        var localSymbols = new uint[symCount];
        for (int i = 0; i < symCount; i++)
            localSymbols[i] = reader.ReadUInt32();

        int valCount = reader.ReadInt32();
        var values = new string[valCount];
        for (int i = 0; i < valCount; i++)
        {
            int len = reader.ReadInt32();
            values[i] = Encoding.UTF8.GetString(reader.ReadBytes(len));
        }

        XElement BuildElement(int index)
        {
            var n = nodes[index];
            var elem = new XElement("Node"); 

            for (int i = n.AttributeStartIndex; i < n.AttributeStartIndex + n.AttributeCount; i++)
            {
                var a = attrs[i];
                uint aGlobalSym = a.NameSymbolId == 0xFFF ? 0xFFFFFFFF : localSymbols[a.NameSymbolId];
                string val = a.ValueIndex == 0xFFF ? "" : values[a.ValueIndex];
                
                string safeVal = EncodeValue(val, out bool isBase64);

                var attrElem = new XElement("Attr",
                    new XAttribute("SymId", aGlobalSym.ToString()),
                    new XAttribute("SymName", symbols.GetSymbolName(aGlobalSym)),
                    new XAttribute("Type", a.Type.ToString()),
                    new XAttribute("Value", safeVal)
                );
                
                if (isBase64) attrElem.Add(new XAttribute("IsBase64", "true"));
                elem.Add(attrElem);
            }

            int childIdx = n.FirstChildIndex;
            while (childIdx != -1)
            {
                elem.Add(BuildElement(childIdx));
                childIdx = nodes[childIdx].NextSiblingIndex;
            }
            return elem;
        }

        var doc = new XDocument(new XElement("AmalurBXML"));
        if (nodeCount > 0) doc.Root!.Add(BuildElement(0));
        return doc;
    }

    public void Write(BinaryWriter writer, XDocument xml, SymbolManager symbols)
    {
        var localSymbols = new List<uint>();
        var values = new List<string>();
        var nodes = new List<BxmlNodeStruct>();
        var attrs = new List<BxmlAttrStruct>();

        int GetSymId(uint globalId)
        {
            if (globalId == 0xFFFFFFFF) return 0xFFF;
            int idx = localSymbols.IndexOf(globalId);
            if (idx == -1) 
            { 
                idx = localSymbols.Count; 
                if (idx >= 0xFFF) throw new Exception("Data limit exceeded: Too many unique symbols.");
                localSymbols.Add(globalId); 
            }
            return idx;
        }

        int GetValIdx(string val)
        {
            int idx = values.IndexOf(val);
            if (idx == -1) 
            { 
                idx = values.Count; 
                if (idx >= 0xFFF) throw new Exception("Data limit exceeded: Too many unique values.");
                values.Add(val); 
            }
            return idx;
        }

        int ProcessElement(XElement elem, int parentIdx)
        {
            int nodeIdx = nodes.Count;
            var n = new BxmlNodeStruct();
            nodes.Add(n); 

            n.ParentIndex = parentIdx;
            n.AttributeStartIndex = attrs.Count;

            foreach (var aElem in elem.Elements("Attr"))
            {
                string symName = aElem.Attribute("SymName")?.Value;
                string symIdStr = aElem.Attribute("SymId")?.Value;
                uint gSym = 0xFFFFFFFF; 
                bool isResolved = false;

                if (!string.IsNullOrWhiteSpace(symName) && !symName.StartsWith("UNKNOWN_"))
                {
                    if (symbols.TryGetSymbolId(symName, out uint mappedId))
                    {
                        gSym = mappedId;
                        isResolved = true;
                    }
                }

                if (!isResolved && !string.IsNullOrWhiteSpace(symIdStr))
                {
                    if (uint.TryParse(symIdStr, out uint parsedId))
                        gSym = parsedId;
                }

                bool isBase64 = aElem.Attribute("IsBase64")?.Value == "true";
                string rawVal = DecodeValue(aElem.Attribute("Value")?.Value ?? "", isBase64);

                byte type = 1;
                if (aElem.Attribute("Type") != null)
                    byte.TryParse(aElem.Attribute("Type")!.Value, out type);

                attrs.Add(new BxmlAttrStruct
                {
                    NameSymbolId = GetSymId(gSym),
                    ValueIndex = GetValIdx(rawVal),
                    Type = type
                });
            }
            n.AttributeCount = attrs.Count - n.AttributeStartIndex;

            n.FirstChildIndex = -1;
            int lastChildIdx = -1;
            foreach (var cElem in elem.Elements("Node"))
            {
                int cIdx = ProcessElement(cElem, nodeIdx);
                if (n.FirstChildIndex == -1) n.FirstChildIndex = cIdx;
                if (lastChildIdx != -1) nodes[lastChildIdx].NextSiblingIndex = cIdx;
                lastChildIdx = cIdx;
            }
            n.NextSiblingIndex = -1;
            nodes[nodeIdx] = n;
            return nodeIdx;
        }

        var rootNode = xml.Root!.Element("Node");
        if (rootNode != null) ProcessElement(rootNode, -1);

        writer.Write(nodes.Count);
        foreach (var n in nodes)
        {
            writer.Write(n.ParentIndex);
            writer.Write(n.FirstChildIndex);
            writer.Write(n.NextSiblingIndex);
            writer.Write(n.AttributeCount);
            writer.Write(n.AttributeStartIndex);
        }

        writer.Write(attrs.Count);
        foreach (var a in attrs)
        {
            uint packed = (uint)(((a.Type & 0xFF) << 24) | ((a.ValueIndex & 0xFFF) << 12) | (a.NameSymbolId & 0xFFF));
            writer.Write(packed);
        }

        writer.Write(localSymbols.Count);
        foreach (var sym in localSymbols) writer.Write(sym);

        writer.Write(values.Count);
        foreach (var val in values)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(val);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        writer.Write(0); 
    }

    private static string EncodeValue(string input, out bool isBase64)
    {
        isBase64 = false;
        if (string.IsNullOrEmpty(input)) return input;
        
        foreach (char c in input)
        {
            if (!System.Xml.XmlConvert.IsXmlChar(c))
            {
                isBase64 = true;
                return Convert.ToBase64String(Encoding.UTF8.GetBytes(input));
            }
        }
        return input;
    }

    private static string DecodeValue(string input, bool isBase64)
    {
        if (isBase64)
            return Encoding.UTF8.GetString(Convert.FromBase64String(input));
        return input;
    }
}