using System.Globalization;
using System.Text;

namespace ValheimWebMap
{
    internal sealed class JsonWriter
    {
        private readonly StringBuilder _sb;
        private bool _needsComma;

        public JsonWriter(int capacity = 256)
        {
            _sb = new StringBuilder(capacity);
        }

        public JsonWriter BeginObject()
        {
            Separator();
            _sb.Append('{');
            _needsComma = false;
            return this;
        }

        public JsonWriter EndObject()
        {
            _sb.Append('}');
            _needsComma = true;
            return this;
        }

        public JsonWriter BeginArray()
        {
            Separator();
            _sb.Append('[');
            _needsComma = false;
            return this;
        }

        public JsonWriter EndArray()
        {
            _sb.Append(']');
            _needsComma = true;
            return this;
        }

        public JsonWriter Key(string name)
        {
            Separator();
            WriteString(name);
            _sb.Append(':');
            _needsComma = false;
            return this;
        }

        public JsonWriter Value(string value)
        {
            Separator();
            if (value == null) _sb.Append("null");
            else WriteString(value);
            _needsComma = true;
            return this;
        }

        public JsonWriter Value(bool value)
        {
            Separator();
            _sb.Append(value ? "true" : "false");
            _needsComma = true;
            return this;
        }

        public JsonWriter Value(long value)
        {
            Separator();
            _sb.Append(value.ToString(CultureInfo.InvariantCulture));
            _needsComma = true;
            return this;
        }

        public JsonWriter Value(double value, int decimals)
        {
            Separator();
            if (double.IsNaN(value) || double.IsInfinity(value)) value = 0;
            _sb.Append(value.ToString("F" + decimals, CultureInfo.InvariantCulture));
            _needsComma = true;
            return this;
        }

        public JsonWriter Prop(string key, string value) => Key(key).Value(value);
        public JsonWriter Prop(string key, bool value) => Key(key).Value(value);
        public JsonWriter Prop(string key, long value) => Key(key).Value(value);
        public JsonWriter Prop(string key, double value, int decimals) => Key(key).Value(value, decimals);

        private void Separator()
        {
            if (_needsComma) _sb.Append(',');
        }

        private void WriteString(string s)
        {
            _sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': _sb.Append("\\\""); break;
                    case '\\': _sb.Append("\\\\"); break;
                    case '\n': _sb.Append("\\n"); break;
                    case '\r': _sb.Append("\\r"); break;
                    case '\t': _sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) _sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else _sb.Append(c);
                        break;
                }
            }
            _sb.Append('"');
        }

        public override string ToString() => _sb.ToString();
    }
}
