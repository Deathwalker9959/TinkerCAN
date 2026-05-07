using System;
using System.Text.RegularExpressions;

namespace TinkerCAN.Lin
{
    /// <summary>
    /// Arithmetic expression evaluator supporting +−*/% &amp; | ^ ~ operators,
    /// parentheses, 0x hex literals, and D0..D63 data-byte variables.
    /// Used by modifier strings like "D0=D0+1" or "D[0..7]=D0^0xFF".
    /// </summary>
    public sealed class Expr
    {
        readonly string _s;
        int _i;

        Expr(string s) { _s = s; _i = 0; }

        void Ws() { while (_i < _s.Length && _s[_i] == ' ') _i++; }
        char Pk() { Ws(); return _i < _s.Length ? _s[_i] : '\0'; }
        char Eat() { Ws(); return _i < _s.Length ? _s[_i++] : '\0'; }

        int Or()  { int v = Xor(); while (Pk() == '|') { Eat(); v |= Xor(); }  return v; }
        int Xor() { int v = And(); while (Pk() == '^') { Eat(); v ^= And(); }  return v; }
        int And() { int v = Add(); while (Pk() == '&') { Eat(); v &= Add(); }  return v; }

        int Add()
        {
            int v = Mul();
            while (Pk() == '+' || Pk() == '-')
            { char op = Eat(); int r = Mul(); v = op == '+' ? v + r : v - r; }
            return v;
        }

        int Mul()
        {
            int v = Unary();
            while (Pk() == '*' || Pk() == '/' || Pk() == '%')
            { char op = Eat(); int r = Unary(); v = op == '*' ? v * r : op == '/' ? v / r : v % r; }
            return v;
        }

        int Unary()
        {
            if (Pk() == '~') { Eat(); return ~Unary(); }
            if (Pk() == '-') { Eat(); return -Unary(); }
            return Atom();
        }

        int Atom()
        {
            if (Pk() == '(') { Eat(); int v = Or(); if (Pk() == ')') Eat(); return v; }
            Ws();
            int s = _i;
            if (_i + 1 < _s.Length && _s[_i] == '0' && (_s[_i + 1] == 'x' || _s[_i + 1] == 'X'))
            {
                _i += 2; int hs = _i;
                while (_i < _s.Length && IsHex(_s[_i])) _i++;
                return Convert.ToInt32(_s[hs.._i], 16);
            }
            while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
            if (_i == s) throw new FormatException($"Unexpected '{Pk()}' in: {_s}");
            return int.Parse(_s[s.._i]);
        }

        static bool IsHex(char c) =>
            char.IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

        /// <summary>
        /// Evaluate <paramref name="expr"/> with D0..D63 substituted from <paramref name="data"/>.
        /// </summary>
        public static int Eval(string expr, byte[] data)
        {
            expr = Regex.Replace(expr.Trim(), @"\bD(\d+)\b", m =>
            {
                int idx = int.Parse(m.Groups[1].Value);
                return idx >= 0 && idx < data.Length ? data[idx].ToString() : "0";
            }, RegexOptions.IgnoreCase);
            return new Expr(expr).Or();
        }
    }
}
