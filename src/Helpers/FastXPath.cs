using System;
using System.Collections;
using System.Collections.Generic;
using System.Xml;

namespace Verse.StartupOptimizer
{
    /// <summary>
    /// Drop-in replacement for XmlDocument.SelectNodes / SelectSingleNode during ApplyPatches.
    ///
    /// PROBLEM: The original code does 13,300 XPath queries on a 100,000-node XmlDocument.
    ///          Each query scans ALL nodes: O(n × m) ≈ 1.3 billion operations → 76 seconds.
    ///
    /// SOLUTION: Pre-build a composite (typeName, defName) → XmlNode hash index.
    ///           Most XPath patterns are "/Defs/ThingDef[@defName='X']/optionalSubPath".
    ///           Composite key avoids false hits when two def types share the same defName.
    ///           Hash lookup O(1), then XPath on ~100-node subtree instead of 100,000-node doc.
    ///
    /// FALLBACK: Any pattern TryExtract cannot safely handle falls back to xml.SelectNodes(xpath).
    ///
    /// INVALIDATION: When Replace/Remove patches a top-level node, ParentNode becomes null.
    ///               LookupDef detects this, falls back to full scan, updates index.
    /// </summary>
    public static class FastXPath
    {
        // Composite key: typeName + NUL + defName  (NUL cannot appear in valid XML names)
        // Using plain string avoids ValueTuple compatibility concerns on older Mono builds.
        private static Dictionary<string, XmlNode> _index;

        // The document _index was built for. FastSelect* only trust the index for this exact
        // document, so a stale index (e.g. if a prior ApplyPatches threw before ClearIndex) is
        // never consulted against a different document.
        private static XmlDocument _indexedDoc;

        // Keys (type+defName) that appeared on more than one top-level node. Real SelectNodes
        // returns ALL of them, so for these we must fall back instead of returning one node.
        private static HashSet<string> _ambiguous;

        private static int _hits;
        private static int _misses;
        private static int _fallbacks;

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>Called at the start of ApplyPatches. Builds O(n) index.</summary>
        public static void BuildIndex(XmlDocument doc)
        {
            _hits = 0; _misses = 0; _fallbacks = 0;

            var root = doc.DocumentElement;
            if (root == null) return;

            var idx = new Dictionary<string, XmlNode>(root.ChildNodes.Count + 512, StringComparer.Ordinal);
            var ambiguous = new HashSet<string>(StringComparer.Ordinal);
            foreach (XmlNode child in root.ChildNodes)
            {
                if (child.NodeType != XmlNodeType.Element) continue;

                // Support BOTH: @defName attribute AND <defName> child element.
                // .Trim() guards against incidental whitespace in XML formatting.
                string defName = child.Attributes?["defName"]?.Value?.Trim()
                              ?? child["defName"]?.InnerText?.Trim();
                if (string.IsNullOrEmpty(defName)) continue;

                string key = MakeKey(child.LocalName, defName);
                if (idx.ContainsKey(key))
                    ambiguous.Add(key);   // duplicate (type, defName): SelectNodes must return all
                else
                    idx[key] = child;     // first writer wins = first in document order
            }
            _index = idx;
            _ambiguous = ambiguous;
            _indexedDoc = doc;
        }

        /// <summary>Free index memory and log diagnostics after ApplyPatches completes.</summary>
        public static void ClearIndex()
        {
            _index = null;
            _indexedDoc = null;
            _ambiguous = null;
            UnityEngine.Debug.Log(
                $"[FastXPath] ApplyPatches complete — hits={_hits}  misses={_misses}  fallbacks={_fallbacks}");
        }

        /// <summary>Replaces xml.SelectNodes(xpath). Called by Cecil-patched ApplyWorker methods.</summary>
        public static XmlNodeList FastSelectNodes(XmlDocument xml, string xpath)
        {
            if (_index != null && ReferenceEquals(xml, _indexedDoc)
                && TryExtract(xpath, out string typeName, out string defName, out string subPath))
            {
                // Multiple defs share this (type, defName): real SelectNodes returns them all,
                // so a single-node answer would silently under-apply the patch. Fall back.
                if (_ambiguous != null && _ambiguous.Contains(MakeKey(typeName, defName)))
                { _fallbacks++; return xml.SelectNodes(xpath); }

                XmlNode def = LookupDef(xml, typeName, defName);
                if (def == null) { _misses++; return xml.SelectNodes(xpath); } // fallback: node may have been added dynamically by a prior PatchOperationAdd

                if (subPath == null) { _hits++; return new SingleList(def); }

                // Query subPath on the indexed node.
                // Guard: subPath must be a valid relative XPath step (not a union expression).
                // If it returns 0 results, fall back to full document XPath.
                try
                {
                    var result = def.SelectNodes(subPath);
                    if (result != null && result.Count > 0) { _hits++; return result; }
                }
                catch { /* invalid subPath (e.g. union '|' leaked through) — fall through to full XPath */ }
            }
            _fallbacks++;
            return xml.SelectNodes(xpath);
        }

        /// <summary>Replaces xml.SelectSingleNode(xpath). Called by Cecil-patched ApplyWorker methods.</summary>
        public static XmlNode FastSelectSingleNode(XmlDocument xml, string xpath)
        {
            if (_index != null && ReferenceEquals(xml, _indexedDoc)
                && TryExtract(xpath, out string typeName, out string defName, out string subPath))
            {
                XmlNode def = LookupDef(xml, typeName, defName);
                if (def == null) { _misses++; return xml.SelectSingleNode(xpath); } // fallback: node may have been added dynamically by a prior PatchOperationAdd

                if (subPath == null) { _hits++; return def; }

                try
                {
                    var result = def.SelectSingleNode(subPath);
                    if (result != null) { _hits++; return result; }
                }
                catch { /* invalid subPath — fall through to full XPath */ }
            }
            _fallbacks++;
            return xml.SelectSingleNode(xpath);
        }

        // ── Index key ─────────────────────────────────────────────────────────

        // NUL separator is safe: it can't appear in XML element names or defName values.
        private static string MakeKey(string typeName, string defName) => typeName + "\0" + defName;

        // ── Index lookup + stale-node invalidation ────────────────────────────

        private static XmlNode LookupDef(XmlDocument xml, string typeName, string defName)
        {
            string key = MakeKey(typeName, defName);
            if (!_index.TryGetValue(key, out XmlNode node))
                return null;

            if (node.ParentNode != null)
                return node; // still in document, fast path

            // Node was removed/replaced by a prior patch — refresh the index entry.
            _index.Remove(key);
            string escaped = EscapeXPathValue(defName);

            // Try exact type first, then wildcard type — both attribute and element defName forms.
            XmlNode newNode =
                xml.SelectSingleNode($"/Defs/{typeName}[@defName=\"{escaped}\"]")
             ?? xml.SelectSingleNode($"/Defs/{typeName}[defName=\"{escaped}\"]")
             ?? xml.SelectSingleNode($"/Defs/*[@defName=\"{escaped}\"]")
             ?? xml.SelectSingleNode($"/Defs/*[defName=\"{escaped}\"]");

            if (newNode != null)
                _index[MakeKey(newNode.LocalName, defName)] = newNode;

            return newNode;
        }

        // ── XPath pattern parser ──────────────────────────────────────────────
        //
        // Recognises patterns of the form:
        //   /Defs/ThingDef[@defName="X"]            typeName="ThingDef", defName="X", sub=null
        //   /Defs/ThingDef[defName="X"]/stats       typeName="ThingDef", defName="X", sub="stats"
        //   //ThingDef[@defName='X']                typeName="ThingDef", defName="X", sub=null
        //   /Defs/ThingDef[defName = "X"]/li/cost   spaces around = are fine
        //
        // Returns false (→ fallback) for anything else:
        //   /Defs/ThingDef/statBases/li[@defName="X"]      defName not at top level
        //   /Defs/ThingDef[@defName="X"]/li[@defName="Y"]  second predicate in sub-path
        //   /Defs/ThingDef[@defName="X" and @Abstract="T"] complex predicate
        private static bool TryExtract(string xpath,
            out string typeName, out string defName, out string subPath)
        {
            typeName = null; defName = null; subPath = null;

            int len = xpath.Length;

            int i = xpath.IndexOf("defName", StringComparison.Ordinal);
            if (i < 0) return false;

            // Find the '[' that opens the predicate containing this defName occurrence.
            int bracketPos = xpath.LastIndexOf('[', i - 1);
            if (bracketPos < 0) return false;

            // Guard 1: no ']' before our '[' — if there is one we're inside a sub-path predicate.
            if (xpath.IndexOf(']', 0, bracketPos) >= 0) return false;

            // Guard 2: path before '[' must be a single-step top-level selector.
            // TrimEnd handles mod XPaths with a space before '[' e.g. "Defs/ThingDef [defName=..."
            string pathBefore = xpath.Substring(0, bracketPos).TrimEnd();
            typeName = ExtractTypeName(pathBefore);
            if (typeName == null) return false;

            // Skip past "defName"
            i += 7;
            if (i >= len) return false;

            // Skip optional '@' prefix (already consumed as part of the preceding chars, ignore)
            while (i < len && xpath[i] == ' ') i++;

            if (i >= len || xpath[i] != '=') return false;
            i++;

            while (i < len && xpath[i] == ' ') i++;

            if (i >= len) return false;
            char q = xpath[i];
            if (q != '"' && q != '\'') return false;
            i++;

            int nameStart = i;
            while (i < len && xpath[i] != q) i++;
            if (i >= len) return false;

            defName = xpath.Substring(nameStart, i - nameStart);
            if (defName.Length == 0) return false;
            i++; // skip closing quote

            // Guard 3: nothing between closing quote and ']' except whitespace → no complex predicate.
            int closing = xpath.IndexOf(']', i);
            if (closing < 0) return false;
            if (xpath.Substring(i, closing - i).Trim().Length > 0) return false;

            i = closing + 1; // skip ']'

            if (i < len)
            {
                // Union expression (e.g. "A|B") — we can only handle single-node paths
                if (xpath[i] == '|') return false;

                if (xpath[i] == '/')
                {
                    i++;
                    if (i < len && xpath[i] == '/') { typeName = null; return false; } // absolute descendant
                }
                string sub = xpath.Substring(i).Trim();
                // Reject if subPath itself is or contains a union operator
                if (sub.Contains("|")) return false;
                subPath = sub.Length > 0 ? sub : null;
            }

            return true;
        }

        // Extracts type name from a top-level path prefix.
        // /Defs/ThingDef → "ThingDef"   //ThingDef → "ThingDef"   ThingDef → "ThingDef"
        // Returns null if path contains extra segments (e.g. /Defs/A/B).
        private static string ExtractTypeName(string path)
        {
            string t;
            if      (path.StartsWith("//",     StringComparison.Ordinal)) t = path.Substring(2);
            else if (path.StartsWith("/Defs/", StringComparison.Ordinal)) t = path.Substring(6);
            else if (path.StartsWith("Defs/",  StringComparison.Ordinal)) t = path.Substring(5);
            else                                                           t = path;

            return (t.Length > 0 && t.IndexOf('/') < 0) ? t : null;
        }

        private static string EscapeXPathValue(string val) => val.Replace("\"", "&quot;");

        // ── Minimal XmlNodeList implementations ──────────────────────────────

        private sealed class EmptyList : XmlNodeList
        {
            public static readonly EmptyList Instance = new EmptyList();
            public override int Count => 0;
            public override XmlNode Item(int index) => null;
            public override IEnumerator GetEnumerator() => Array.Empty<XmlNode>().GetEnumerator();
        }

        private sealed class SingleList : XmlNodeList
        {
            private readonly XmlNode _node;
            public SingleList(XmlNode node) => _node = node;
            public override int Count => 1;
            public override XmlNode Item(int index) => index == 0 ? _node : null;
            public override IEnumerator GetEnumerator() { yield return _node; }
        }
    }
}
