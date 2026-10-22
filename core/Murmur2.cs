using System;
using System.IO;

namespace Mcroute
{
    /// <summary>
    /// MurmurHash2（32 位，seed=1），CurseForge 的文件指纹算法。
    ///
    /// CurseForge 的规则：先去掉文件里的空白字节（0x09 制表、0x0A 换行、0x0D 回车、0x20 空格），
    /// 再对剩余字节做 MurmurHash2（seed = 1）。
    ///
    /// 为什么不用 SHA1：Modrinth 用 SHA1 反查，CurseForge 只认这个指纹——
    /// 同一个 jar 在两家平台的标识方式不同。
    /// </summary>
    public static class Murmur2
    {
        public static uint Hash(byte[] data, uint seed = 1)
        {
            if (data == null) data = new byte[0];
            const uint m = 0x5bd1e995;
            const int r = 24;

            uint h = seed ^ (uint)data.Length;
            int i = 0;
            int len = data.Length;

            while (len >= 4)
            {
                uint k = (uint)(data[i] | (data[i + 1] << 8) | (data[i + 2] << 16) | (data[i + 3] << 24));
                k *= m;
                k ^= k >> r;
                k *= m;
                h *= m;
                h ^= k;
                i += 4;
                len -= 4;
            }

            switch (len)
            {
                case 3: h ^= (uint)data[i + 2] << 16; goto case 2;
                case 2: h ^= (uint)data[i + 1] << 8; goto case 1;
                case 1: h ^= data[i]; h *= m; break;
            }

            h ^= h >> 13;
            h *= m;
            h ^= h >> 15;
            return h;
        }

        /// <summary>对文件算 CurseForge 指纹（去掉空白字节后做 MurmurHash2，seed=1）。</summary>
        public static uint Fingerprint(string path)
        {
            byte[] raw = File.ReadAllBytes(path);
            int n = 0;
            for (int i = 0; i < raw.Length; i++)
            {
                byte b = raw[i];
                if (b == 0x09 || b == 0x0A || b == 0x0D || b == 0x20) continue;
                raw[n++] = b;
            }
            return Hash(raw, 1, n);
        }

        /// <summary>对已过滤字节数组的前 n 个字节做哈希（避免额外分配）。</summary>
        public static uint Hash(byte[] data, uint seed, int length)
        {
            if (data == null) return Hash(new byte[0], seed);
            const uint m = 0x5bd1e995;
            const int r = 24;

            uint h = seed ^ (uint)length;
            int i = 0;
            int len = length;

            while (len >= 4)
            {
                uint k = (uint)(data[i] | (data[i + 1] << 8) | (data[i + 2] << 16) | (data[i + 3] << 24));
                k *= m;
                k ^= k >> r;
                k *= m;
                h *= m;
                h ^= k;
                i += 4;
                len -= 4;
            }

            switch (len)
            {
                case 3: h ^= (uint)data[i + 2] << 16; goto case 2;
                case 2: h ^= (uint)data[i + 1] << 8; goto case 1;
                case 1: h ^= data[i]; h *= m; break;
            }

            h ^= h >> 13;
            h *= m;
            h ^= h >> 15;
            return h;
        }
    }
}
