using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace FactoryFlow.PlcClient.Services
{
    public static class PlcTagMap
    {
        public static IReadOnlyDictionary<string, string> Load(string? path)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(path)) return map;
            using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var document = XDocument.Load(reader);
            foreach (var tag in document.Root!.Elements("Tag"))
            {
                var alias = (string?)tag.Attribute("Alias"); var identifier = (string?)tag.Attribute("Identifier");
                if (string.IsNullOrWhiteSpace(alias) || string.IsNullOrWhiteSpace(identifier) || map.ContainsKey(alias!))
                    throw new InvalidDataException("Tag map contains missing or duplicate aliases.");
                map.Add(alias!, identifier!);
            }
            return map;
        }
    }
}
