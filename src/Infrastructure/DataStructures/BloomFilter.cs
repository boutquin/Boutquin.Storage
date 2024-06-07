using Boutquin.Storage.Infrastructure.Algorithms;

/// <summary>
/// Represents a Bloom filter, a probabilistic data structure used to test whether an element is a member of a set.
/// This implementation uses Murmur3 and xxHash32 as hash functions to ensure efficient and fast hashing.
/// 
/// &lt;para&gt;This Bloom filter implementation is highly space-efficient and allows for fast insertions and membership checks.
/// It can report false positives but will never report false negatives.&lt;/para&gt;
/// 
/// &lt;para&gt;Implementation Choices:&lt;/para&gt;
/// &lt;para&gt;This implementation makes specific choices to balance performance and memory usage:
/// &lt;list type="bullet"&gt;
/// &lt;item&gt;
/// &lt;description&gt;&lt;b&gt;Bit Array Size (m):&lt;/b&gt; The number of bits in the Bloom filter is chosen based on the expected number of elements and the desired false positive probability.&lt;/description&gt;
/// &lt;/item&gt;
/// &lt;item&gt;
/// &lt;description&gt;&lt;b&gt;Number of Hash Functions (k):&lt;/b&gt; The number of different hash functions is derived from the bit array size and the expected number of elements.&lt;/description&gt;
/// &lt;/item&gt;
/// &lt;item&gt;
/// &lt;description&gt;&lt;b&gt;Hash Functions:&lt;/b&gt; Uses Murmur3 and xxHash32 to provide a good balance of speed and low collision rates.&lt;/description&gt;
/// &lt;/item&gt;
/// &lt;/list&gt;
/// &lt;/para&gt;
/// 
/// &lt;para&gt;Typical Uses:&lt;/para&gt;
/// &lt;para&gt;In the context of a storage engine, a Bloom filter is often used to reduce the number of disk lookups.
/// Before performing a disk read operation to fetch a value, the Bloom filter can be checked to see if the key likely exists in the dataset.
/// If the Bloom filter indicates that the key does not exist, the costly disk read can be skipped, thereby improving performance.&lt;/para&gt;
/// 
/// &lt;para&gt;Example:&lt;/para&gt;
/// &lt;code&gt;
/// var expectedElements = 1000;
/// var falsePositiveProbability = 0.01; // 1% false positive rate
/// var bloomFilter = new BloomFilter&lt;string&gt;(expectedElements, falsePositiveProbability);
/// 
/// bloomFilter.Add("exampleKey");
/// bool exists = bloomFilter.Contains("exampleKey"); // True
/// bloomFilter.Clear();
/// &lt;/code&gt;
/// </summary>
/// &lt;typeparam name="T"&gt;The type of elements to be stored in the Bloom filter.&lt;/typeparam&gt;
public class BloomFilter<T> : IBloomFilter<T>
{
    private readonly BitArray _bitArray;
    private readonly int _hashFunctionCount;
    private readonly int _bitArraySize;
    private readonly Func<T, byte[]> _itemToBytes;
    private readonly IHashAlgorithm _hashAlgorithm1;
    private readonly IHashAlgorithm _hashAlgorithm2;

    /// <summary>
    /// Initializes a new instance of the &lt;see cref="BloomFilter{T}" /&gt; class with expected elements and false positive probability.
    /// This constructor uses Murmur3 and xxHash32 as default hash algorithms.
    /// </summary>
    /// &lt;param name="expectedElements"&gt;The expected number of elements to store in the Bloom filter.&lt;/param&gt;
    /// &lt;param name="falsePositiveProbability"&gt;The desired false positive probability.&lt;/param&gt;
    /// &lt;example&gt;
    /// &lt;code&gt;
    /// var expectedElements = 1000;
    /// var falsePositiveProbability = 0.01; // 1% false positive rate
    /// var bloomFilter = new BloomFilter&lt;string&gt;(expectedElements, falsePositiveProbability);
    /// &lt;/code&gt;
    /// &lt;/example&gt;
    public BloomFilter(int expectedElements, double falsePositiveProbability)
        : this(expectedElements, falsePositiveProbability, new Murmur3(), new XxHash32(), DefaultItemToBytes)
    {
    }

    /// <summary>
    /// Initializes a new instance of the &lt;see cref="BloomFilter{T}" /&gt; class with custom hash functions.
    /// </summary>
    /// &lt;param name="expectedElements"&gt;The expected number of elements to store in the Bloom filter.&lt;/param&gt;
    /// &lt;param name="falsePositiveProbability"&gt;The desired false positive probability.&lt;/param&gt;
    /// &lt;param name="hashAlgorithm1"&gt;The first hash algorithm.&lt;/param&gt;
    /// &lt;param name="hashAlgorithm2"&gt;The second hash algorithm.&lt;/param&gt;
    /// &lt;param name="itemToBytes"&gt;A function to convert items to byte arrays.&lt;/param&gt;
    /// &lt;example&gt;
    /// &lt;code&gt;
    /// var expectedElements = 1000;
    /// var falsePositiveProbability = 0.01; // 1% false positive rate
    /// var bloomFilter = new BloomFilter&lt;string&gt;(
    ///     expectedElements, 
    ///     falsePositiveProbability, 
    ///     new Murmur3(), 
    ///     new XxHash32(), 
    ///     item => Encoding.UTF8.GetBytes(item));
    /// &lt;/code&gt;
    /// &lt;/example&gt;
    public BloomFilter(int expectedElements, double falsePositiveProbability, IHashAlgorithm hashAlgorithm1, IHashAlgorithm hashAlgorithm2, Func<T, byte[]> itemToBytes)
    {
        _bitArraySize = CalculateOptimalBitArraySize(expectedElements, falsePositiveProbability);
        _hashFunctionCount = CalculateOptimalHashFunctionCount(expectedElements, _bitArraySize);
        _bitArray = new BitArray(_bitArraySize);
        _itemToBytes = itemToBytes ?? throw new ArgumentNullException(nameof(itemToBytes));
        _hashAlgorithm1 = hashAlgorithm1 ?? throw new ArgumentNullException(nameof(hashAlgorithm1));
        _hashAlgorithm2 = hashAlgorithm2 ?? throw new ArgumentNullException(nameof(hashAlgorithm2));
    }

    /// <summary>
    /// Initializes a new instance of the &lt;see cref="BloomFilter{T}" /&gt; class with a specified bit array size and number of hash functions.
    /// This constructor uses Murmur3 and xxHash32 as default hash algorithms.
    /// </summary>
    /// &lt;param name="bitArraySize"&gt;The size of the bit array.&lt;/param&gt;
    /// &lt;param name="hashFunctionCount"&gt;The number of hash functions.&lt;/param&gt;
    /// &lt;example&gt;
    /// &lt;code&gt;
    /// var bitArraySize = 10000;
    /// var hashFunctionCount = 7;
    /// var bloomFilter = new BloomFilter&lt;string&gt;(bitArraySize, hashFunctionCount);
    /// &lt;/code&gt;
    /// &lt;/example&gt;
    public BloomFilter(int bitArraySize, int hashFunctionCount)
        : this(bitArraySize, hashFunctionCount, new Murmur3(), new XxHash32(), DefaultItemToBytes)
    {
    }

    /// <summary>
    /// Initializes a new instance of the &lt;see cref="BloomFilter{T}" /&gt; class with custom hash functions and a specified bit array size and number of hash functions.
    /// </summary>
    /// &lt;param name="bitArraySize"&gt;The size of the bit array.&lt;/param&gt;
    /// &lt;param name="hashFunctionCount"&gt;The number of hash functions.&lt;/param&gt;
    /// &lt;param name="hashAlgorithm1"&gt;The first hash algorithm.&lt;/param&gt;
    /// &lt;param name="hashAlgorithm2"&gt;The second hash algorithm.&lt;/param&gt;
    /// &lt;param name="itemToBytes"&gt;A function to convert items to byte arrays.&lt;/param&gt;
    /// &lt;example&gt;
    /// &lt;code&gt;
    /// var bitArraySize = 10000;
    /// var hashFunctionCount = 7;
    /// var bloomFilter = new BloomFilter&lt;string&gt;(
    ///     bitArraySize, 
    ///     hashFunctionCount, 
    ///     new Murmur3(), 
    ///     new XxHash32(), 
    ///     item => Encoding.UTF8.GetBytes(item));
    /// &lt;/code&gt;
    /// &lt;/example&gt;
    public BloomFilter(int bitArraySize, int hashFunctionCount, IHashAlgorithm hashAlgorithm1, IHashAlgorithm hashAlgorithm2, Func<T, byte[]> itemToBytes)
    {
        _bitArraySize = bitArraySize;
        _hashFunctionCount = hashFunctionCount;
        _bitArray = new BitArray(_bitArraySize);
        _itemToBytes = itemToBytes ?? throw new ArgumentNullException(nameof(itemToBytes));
        _hashAlgorithm1 = hashAlgorithm1 ?? throw new ArgumentNullException(nameof(hashAlgorithm1));
        _hashAlgorithm2 = hashAlgorithm2 ?? throw new ArgumentNullException(nameof(hashAlgorithm2));
    }

    /// <summary>
    /// Adds an element to the Bloom filter.
    /// </summary>
    /// &lt;param name="item"&gt;The item to add.&lt;/param&gt;
    /// &lt;example&gt;
    /// &lt;code&gt;
    /// var bloomFilter = new BloomFilter&lt;string&gt;(1000, 0.01);
    /// bloomFilter.Add("exampleKey");
    /// &lt;/code&gt;
    /// &lt;/example&gt;
    public void Add(T item)
    {
        foreach (var position in GetHashPositions(item))
        {
            _bitArray[position] = true;
        }
    }

    /// <summary>
    /// Checks if an element is possibly in the Bloom filter.
    /// </summary>
    /// &lt;param name="item"&gt;The item to check.&lt;/param&gt;
    /// &lt;returns&gt;True if the item is possibly in the filter; false if the item is definitely not in the filter.&lt;/returns&gt;
    /// &lt;example&gt;
    /// &lt;code&gt;
    /// var bloomFilter = new BloomFilter&lt;string&gt;(1000, 0.01);
    /// bloomFilter.Add("exampleKey");
    /// bool exists = bloomFilter.Contains("exampleKey"); // True
    /// &lt;/code&gt;
    /// &lt;/example&gt;
    public bool Contains(T item)
    {
        foreach (var position in GetHashPositions(item))
        {
            if (!_bitArray[position])
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Clears all elements from the Bloom filter.
    /// </summary>
    /// &lt;example&gt;
    /// &lt;code&gt;
    /// var bloomFilter = new BloomFilter&lt;string&gt;(1000, 0.01);
    /// bloomFilter.Add("exampleKey");
    /// bloomFilter.Clear();
    /// bool exists = bloomFilter.Contains("exampleKey"); // False
    /// &lt;/code&gt;
    /// &lt;/example&gt;
    public void Clear()
    {
        _bitArray.SetAll(false);
    }

    /// <summary>
    /// Calculates the optimal size of the bit array.
    /// </summary>
    /// &lt;param name="n"&gt;The expected number of elements.&lt;/param&gt;
    /// &lt;param name="p"&gt;The desired false positive probability.&lt;/param&gt;
    /// &lt;returns&gt;The optimal size of the bit array.&lt;/returns&gt;
    private static int CalculateOptimalBitArraySize(int n, double p)
    {
        return (int)(-n * Math.Log(p) / (Math.Pow(Math.Log(2), 2)));
    }

    /// <summary>
    /// Calculates the optimal number of hash functions.
    /// </summary>
    /// &lt;param name="n"&gt;The expected number of elements.&lt;/param&gt;
    /// &lt;param name="m"&gt;The size of the bit array.&lt;/param&gt;
    /// &lt;returns&gt;The optimal number of hash functions.&lt;/returns&gt;
    private static int CalculateOptimalHashFunctionCount(int n, int m)
    {
        return (int)(m / n * Math.Log(2));
    }

    /// <summary>
    /// Generates the hash positions for the given item using double hashing.
    /// </summary>
    /// &lt;param name="item"&gt;The item to hash.&lt;/param&gt;
    /// &lt;returns&gt;A list of positions in the bit array.&lt;/returns&gt;
    /// &lt;remarks&gt;
    /// This method combines two hash functions, Murmur3 and xxHash32, to generate multiple hash values for the item.
    /// Double hashing helps to reduce the likelihood of hash collisions and improves the distribution of bits in the array.
    /// &lt;/remarks&gt;
    private IEnumerable<int> GetHashPositions(T item)
    {
        var itemBytes = _itemToBytes(item);
        var hash1 = (int)_hashAlgorithm1.ComputeHash(itemBytes);
        var hash2 = (int)_hashAlgorithm2.ComputeHash(itemBytes);

        for (int i = 0; i < _hashFunctionCount; i++)
        {
            var combinedHash = (hash1 + i * hash2) % _bitArraySize;
            yield return Math.Abs(combinedHash);
        }
    }

    /// <summary>
    /// Default method to convert an item to a byte array.
    /// </summary>
    /// &lt;param name="item"&gt;The item to convert.&lt;/param&gt;
    /// &lt;returns&gt;The byte array representation of the item.&lt;/returns&gt;
    private static byte[] DefaultItemToBytes(T item)
    {
        return Encoding.UTF8.GetBytes(item.ToString());
    }
}
