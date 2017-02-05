﻿using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Store;
using Lucene.Net.Util;
using System;
using System.Collections.Generic;
using Directory = Lucene.Net.Store.Directory;

namespace Lucene.Net.Search.Spell
{
    /*
     * Licensed to the Apache Software Foundation (ASF) under one or more
     * contributor license agreements.  See the NOTICE file distributed with
     * this work for additional information regarding copyright ownership.
     * The ASF licenses this file to You under the Apache License, Version 2.0
     * (the "License"); you may not use this file except in compliance with
     * the License.  You may obtain a copy of the License at
     *
     *     http://www.apache.org/licenses/LICENSE-2.0
     *
     * Unless required by applicable law or agreed to in writing, software
     * distributed under the License is distributed on an "AS IS" BASIS,
     * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
     * See the License for the specific language governing permissions and
     * limitations under the License.
     */

    /// <summary>
    /// <para>
    ///   Spell Checker class  (Main class) <br/>
    ///  (initially inspired by the David Spencer code).
    /// </para>
    /// 
    /// <para>Example Usage (C#):
    /// 
    /// <code>
    ///  SpellChecker spellchecker = new SpellChecker(spellIndexDirectory);
    ///  // To index a field of a user index:
    ///  spellchecker.IndexDictionary(new LuceneDictionary(my_lucene_reader, a_field));
    ///  // To index a file containing words:
    ///  spellchecker.IndexDictionary(new PlainTextDictionary(new FileInfo("myfile.txt")));
    ///  string[] suggestions = spellchecker.SuggestSimilar("misspelt", 5);
    /// </code>
    /// 
    /// </para>
    /// </summary>
    public class SpellChecker : IDisposable
    {

        /// <summary>
        /// The default minimum score to use, if not specified by setting <see cref="Accuracy"/>
        /// or overriding with <see cref="SuggestSimilar(string, int, IndexReader, string, SuggestMode, float)"/> .
        /// </summary>
        public const float DEFAULT_ACCURACY = 0.5f;

        /// <summary>
        /// Field name for each word in the ngram index.
        /// </summary>
        public const string F_WORD = "word";

        /// <summary>
        /// the spell index
        /// </summary>
        // don't modify the directory directly - see SwapSearcher()
        // TODO: why is this package private?
        internal Directory spellIndex;
        /// <summary>
        /// Boost value for start and end grams
        /// </summary>
        private float bStart = 2.0f;

        private float bEnd = 1.0f;

        // don't use this searcher directly - see SwapSearcher()
        private IndexSearcher searcher;

        /// <summary>
        /// this locks all modifications to the current searcher.
        /// </summary>
        private readonly object searcherLock = new object();

        /// <summary>
        /// this lock synchronizes all possible modifications to the
        /// current index directory.It should not be possible to try modifying
        /// the same index concurrently.Note: Do not acquire the searcher lock
        /// before acquiring this lock!
        /// </summary>
        private readonly object modifyCurrentIndexLock = new object();

        private volatile bool disposed = false;

        // minimum score for hits generated by the spell checker query
        private float accuracy = DEFAULT_ACCURACY;

        private IStringDistance sd;
        private IComparer<SuggestWord> comparer;

        /// <summary>
        /// Use the given directory as a spell checker index. The directory
        /// is created if it doesn't exist yet. </summary>
        /// <param name="spellIndex"> the spell index directory </param>
        /// <param name="sd"> the <see cref="StringDistance"/> measurement to use </param>
        /// <exception cref="System.IO.IOException"> if Spellchecker can not open the directory </exception>
        public SpellChecker(Directory spellIndex, IStringDistance sd)
            : this(spellIndex, sd, SuggestWordQueue.DEFAULT_COMPARER)
        {
        }
        /// <summary>
        /// Use the given directory as a spell checker index with a
        /// <see cref="LevensteinDistance"/> as the default <see cref="T:StringDistance"/>. The
        /// directory is created if it doesn't exist yet.
        /// </summary>
        /// <param name="spellIndex">
        ///          the spell index directory </param>
        /// <exception cref="System.IO.IOException">
        ///           if spellchecker can not open the directory </exception>
        public SpellChecker(Directory spellIndex)
            : this(spellIndex, new LevensteinDistance())
        {
        }

        /// <summary>
        /// Use the given directory as a spell checker index with the given <see cref="IStringDistance"/> measure
        /// and the given <see cref="System.Collections.Generic.IComparer{T}"/> for sorting the results. </summary>
        /// <param name="spellIndex"> The spelling index </param>
        /// <param name="sd"> The distance </param>
        /// <param name="comparer"> The comparer </param>
        /// <exception cref="System.IO.IOException"> if there is a problem opening the index </exception>
        public SpellChecker(Directory spellIndex, IStringDistance sd, IComparer<SuggestWord> comparer)
        {
            SetSpellIndex(spellIndex);
            StringDistance = sd;
            this.comparer = comparer;
        }

        /// <summary>
        /// Sets a different index as the spell checker index or re-open
        /// the existing index if <code>spellIndex</code> is the same value
        /// as given in the constructor. </summary>
        /// <param name="spellIndexDir"> the spell directory to use </param>
        /// <exception cref="AlreadyClosedException"> if the Spellchecker is already closed </exception>
        /// <exception cref="System.IO.IOException"> if spellchecker can not open the directory </exception>
        // TODO: we should make this final as it is called in the constructor
        public virtual void SetSpellIndex(Directory spellIndexDir)
        {
            // this could be the same directory as the current spellIndex
            // modifications to the directory should be synchronized 
            lock (modifyCurrentIndexLock)
            {
                EnsureOpen();
                if (!DirectoryReader.IndexExists(spellIndexDir))
                {
#pragma warning disable 612, 618
                    using (var writer = new IndexWriter(spellIndexDir, new IndexWriterConfig(LuceneVersion.LUCENE_CURRENT, null)))
                    {
                    }
#pragma warning restore 612, 618
                }
                SwapSearcher(spellIndexDir);
            }
        }

        /// <summary>
        /// Gets or sets the <see cref="IComparer{T}"/> for the <see cref="SuggestWordQueue"/>.
        /// </summary>
        public virtual IComparer<SuggestWord> Comparer
        {
            set
            {
                this.comparer = value;
            }
            get
            {
                return comparer;
            }
        }


        /// <summary>
        /// Gets or sets the <see cref="T:StringDistance"/> implementation for this
        /// <see cref="SpellChecker"/> instance.
        /// </summary>
        public virtual IStringDistance StringDistance
        {
            set
            {
                this.sd = value;
            }
            get
            {
                return sd;
            }
        }

        /// <summary>
        /// Gets or sets the accuracy (minimum score) to be used, unless overridden in 
        /// <see cref="SuggestSimilar(string, int, IndexReader, string, SuggestMode, float)"/>, 
        /// to decide whether a suggestion is included or not.
        /// Sets the accuracy 0 &lt; minScore &lt; 1; default <see cref="DEFAULT_ACCURACY"/>
        /// </summary>
        public virtual float Accuracy
        {
            set
            {
                this.accuracy = value;
            }
            get
            {
                return accuracy;
            }
        }


        /// <summary>
        /// Suggest similar words.
        /// <para>
        /// As the Lucene similarity that is used to fetch the most relevant n-grammed terms
        /// is not the same as the edit distance strategy used to calculate the best
        /// matching spell-checked word from the hits that Lucene found, one usually has
        /// to retrieve a couple of numSug's in order to get the true best match.
        /// </para>
        /// <para>
        /// I.e. if numSug == 1, don't count on that suggestion being the best one.
        /// Thus, you should set this value to <b>at least</b> 5 for a good suggestion.
        /// </para>
        /// </summary>
        /// <param name="word"> the word you want a spell check done on </param>
        /// <param name="numSug"> the number of suggested words </param>
        /// <exception cref="System.IO.IOException"> if the underlying index throws an <see cref="System.IO.IOException"/> </exception>
        /// <exception cref="AlreadyClosedException"> if the Spellchecker is already disposed </exception>
        /// <returns>string[] the sorted list of the suggest words with these 2 criteria:
        /// first criteria: the edit distance, second criteria (only if restricted mode): the popularity
        /// of the suggest words in the field of the user index</returns>
        /// <seealso cref="SuggestSimilar(string, int, IndexReader, string, SuggestMode, float)"/>
        public virtual string[] SuggestSimilar(string word, int numSug)
        {
            return this.SuggestSimilar(word, numSug, null, null, SuggestMode.SUGGEST_WHEN_NOT_IN_INDEX);
        }

        /// <summary>
        /// Suggest similar words.
        /// <para>
        /// As the Lucene similarity that is used to fetch the most relevant n-grammed terms
        /// is not the same as the edit distance strategy used to calculate the best
        /// matching spell-checked word from the hits that Lucene found, one usually has
        /// to retrieve a couple of numSug's in order to get the true best match.
        /// </para>
        /// <para>
        /// I.e. if numSug == 1, don't count on that suggestion being the best one.
        /// Thus, you should set this value to <b>at least</b> 5 for a good suggestion.
        /// </para>
        /// </summary>
        /// <param name="word"> the word you want a spell check done on </param>
        /// <param name="numSug"> the number of suggested words </param>
        /// <param name="accuracy"> The minimum score a suggestion must have in order to qualify for inclusion in the results </param>
        /// <exception cref="System.IO.IOException"> if the underlying index throws an <see cref="System.IO.IOException"/> </exception>
        /// <exception cref="AlreadyClosedException"> if the Spellchecker is already disposed </exception>
        /// <returns>string[] the sorted list of the suggest words with these 2 criteria:
        /// first criteria: the edit distance, second criteria (only if restricted mode): the popularity
        /// of the suggest words in the field of the user index</returns>
        /// <seealso cref="SuggestSimilar(string, int, IndexReader, string, SuggestMode, float)"/>
        public virtual string[] SuggestSimilar(string word, int numSug, float accuracy)
        {
            return this.SuggestSimilar(word, numSug, null, null, SuggestMode.SUGGEST_WHEN_NOT_IN_INDEX, accuracy);
        }

        /// <summary>
        /// Calls <see cref="SuggestSimilar(string, int, IndexReader, string, SuggestMode, float)"/>
        ///       SuggestSimilar(word, numSug, ir, suggestMode, field, this.accuracy)
        /// 
        /// </summary>
        public virtual string[] SuggestSimilar(string word, int numSug, IndexReader ir, string field, SuggestMode suggestMode)
        {
            return SuggestSimilar(word, numSug, ir, field, suggestMode, this.accuracy);
        }

        /// <summary>
        /// Suggest similar words (optionally restricted to a field of an index).
        /// <para>
        /// As the Lucene similarity that is used to fetch the most relevant n-grammed terms
        /// is not the same as the edit distance strategy used to calculate the best
        /// matching spell-checked word from the hits that Lucene found, one usually has
        /// to retrieve a couple of numSug's in order to get the true best match.
        /// </para>
        /// <para>
        /// I.e. if numSug == 1, don't count on that suggestion being the best one.
        /// Thus, you should set this value to <b>at least</b> 5 for a good suggestion.
        /// </para>
        /// </summary>
        /// <param name="word"> the word you want a spell check done on </param>
        /// <param name="numSug"> the number of suggested words </param>
        /// <param name="ir"> the indexReader of the user index (can be null see field param) </param>
        /// <param name="field"> the field of the user index: if field is not null, the suggested
        /// words are restricted to the words present in this field. </param>
        /// <param name="suggestMode"> 
        /// (NOTE: if indexReader==null and/or field==null, then this is overridden with SuggestMode.SUGGEST_ALWAYS) </param>
        /// <param name="accuracy"> The minimum score a suggestion must have in order to qualify for inclusion in the results </param>
        /// <exception cref="System.IO.IOException"> if the underlying index throws an <see cref="System.IO.IOException"/> </exception>
        /// <exception cref="AlreadyClosedException"> if the <see cref="SpellChecker"/> is already disposed </exception>
        /// <returns> string[] the sorted list of the suggest words with these 2 criteria:
        /// first criteria: the edit distance, second criteria (only if restricted mode): the popularity
        /// of the suggest words in the field of the user index
        ///  </returns>
        public virtual string[] SuggestSimilar(string word, int numSug, IndexReader ir, string field, SuggestMode suggestMode, float accuracy)
        {
            // obtainSearcher calls ensureOpen
            IndexSearcher indexSearcher = ObtainSearcher();
            try
            {
                if (ir == null || field == null)
                {
                    suggestMode = SuggestMode.SUGGEST_ALWAYS;
                }
                if (suggestMode == SuggestMode.SUGGEST_ALWAYS)
                {
                    ir = null;
                    field = null;
                }

                int lengthWord = word.Length;

                int freq = (ir != null && field != null) ? ir.DocFreq(new Term(field, word)) : 0;
                int goalFreq = suggestMode == SuggestMode.SUGGEST_MORE_POPULAR ? freq : 0;
                // if the word exists in the real index and we don't care for word frequency, return the word itself
                if (suggestMode == SuggestMode.SUGGEST_WHEN_NOT_IN_INDEX && freq > 0)
                {
                    return new string[] { word };
                }

                BooleanQuery query = new BooleanQuery();
                string[] grams;
                string key;

                for (int ng = GetMin(lengthWord); ng <= GetMax(lengthWord); ng++)
                {

                    key = "gram" + ng; // form key

                    grams = FormGrams(word, ng); // form word into ngrams (allow dups too)

                    if (grams.Length == 0)
                    {
                        continue; // hmm
                    }

                    if (bStart > 0) // should we boost prefixes?
                    {
                        Add(query, "start" + ng, grams[0], bStart); // matches start of word

                    }
                    if (bEnd > 0) // should we boost suffixes
                    {
                        Add(query, "end" + ng, grams[grams.Length - 1], bEnd); // matches end of word

                    }
                    for (int i = 0; i < grams.Length; i++)
                    {
                        Add(query, key, grams[i]);
                    }
                }

                int maxHits = 10 * numSug;

                //    System.out.println("Q: " + query);
                ScoreDoc[] hits = indexSearcher.Search(query, null, maxHits).ScoreDocs;
                //    System.out.println("HITS: " + hits.length());
                SuggestWordQueue sugQueue = new SuggestWordQueue(numSug, comparer);

                // go thru more than 'maxr' matches in case the distance filter triggers
                int stop = Math.Min(hits.Length, maxHits);
                SuggestWord sugWord = new SuggestWord();
                for (int i = 0; i < stop; i++)
                {

                    sugWord.String = indexSearcher.Doc(hits[i].Doc).Get(F_WORD); // get orig word

                    // don't suggest a word for itself, that would be silly
                    if (sugWord.String.Equals(word))
                    {
                        continue;
                    }

                    // edit distance
                    sugWord.Score = sd.GetDistance(word, sugWord.String);
                    if (sugWord.Score < accuracy)
                    {
                        continue;
                    }

                    if (ir != null && field != null) // use the user index
                    {
                        sugWord.Freq = ir.DocFreq(new Term(field, sugWord.String)); // freq in the index
                        // don't suggest a word that is not present in the field
                        if ((suggestMode == SuggestMode.SUGGEST_MORE_POPULAR && goalFreq > sugWord.Freq) || sugWord.Freq < 1)
                        {
                            continue;
                        }
                    }
                    sugQueue.InsertWithOverflow(sugWord);
                    if (sugQueue.Count == numSug)
                    {
                        // if queue full, maintain the minScore score
                        accuracy = sugQueue.Top.Score;
                    }
                    sugWord = new SuggestWord();
                }

                // convert to array string
                string[] list = new string[sugQueue.Count];
                for (int i = sugQueue.Count - 1; i >= 0; i--)
                {
                    list[i] = sugQueue.Pop().String;
                }

                return list;
            }
            finally
            {
                ReleaseSearcher(indexSearcher);
            }
        }
        /// <summary>
        /// Add a clause to a boolean query.
        /// </summary>
        private static void Add(BooleanQuery q, string name, string value, float boost)
        {
            Query tq = new TermQuery(new Term(name, value));
            tq.Boost = boost;
            q.Add(new BooleanClause(tq, Occur.SHOULD));
        }

        /// <summary>
        /// Add a clause to a boolean query.
        /// </summary>
        private static void Add(BooleanQuery q, string name, string value)
        {
            q.Add(new BooleanClause(new TermQuery(new Term(name, value)), Occur.SHOULD));
        }

        /// <summary>
        /// Form all ngrams for a given word. </summary>
        /// <param name="text"> the word to parse </param>
        /// <param name="ng"> the ngram length e.g. 3 </param>
        /// <returns> an array of all ngrams in the word and note that duplicates are not removed </returns>
        private static string[] FormGrams(string text, int ng)
        {
            int len = text.Length;
            string[] res = new string[len - ng + 1];
            for (int i = 0; i < len - ng + 1; i++)
            {
                res[i] = text.Substring(i, ng);
            }
            return res;
        }

        /// <summary>
        /// Removes all terms from the spell check index. </summary>
        /// <exception cref="System.IO.IOException"> If there is a low-level I/O error. </exception>
        /// <exception cref="AlreadyClosedException"> if the Spellchecker is already closed </exception>
        public virtual void ClearIndex()
        {
            lock (modifyCurrentIndexLock)
            {
                EnsureOpen();
                var dir = this.spellIndex;
#pragma warning disable 612, 618
                using (var writer = new IndexWriter(dir, new IndexWriterConfig(LuceneVersion.LUCENE_CURRENT, null)
                   .SetOpenMode(OpenMode.CREATE))) { }
#pragma warning restore 612, 618
                SwapSearcher(dir);
            }
        }

        /// <summary>
        /// Check whether the word exists in the index. </summary>
        /// <param name="word"> word to check </param>
        /// <exception cref="System.IO.IOException"> If there is a low-level I/O error. </exception>
        /// <exception cref="AlreadyClosedException"> if the <see cref="SpellChecker"/> is already disposed </exception>
        /// <returns> true if the word exists in the index </returns>
        public virtual bool Exist(string word)
        {
            // obtainSearcher calls ensureOpen
            IndexSearcher indexSearcher = ObtainSearcher();
            try
            {
                // TODO: we should use ReaderUtil+seekExact, we dont care about the docFreq
                // this is just an existence check
                return indexSearcher.IndexReader.DocFreq(new Term(F_WORD, word)) > 0;
            }
            finally
            {
                ReleaseSearcher(indexSearcher);
            }
        }

        /// <summary>
        /// Indexes the data from the given <see cref="IDictionary"/>. </summary>
        /// <param name="dict"> Dictionary to index </param>
        /// <param name="config"> <see cref="IndexWriterConfig"/> to use </param>
        /// <param name="fullMerge"> whether or not the spellcheck index should be fully merged </param>
        /// <exception cref="AlreadyClosedException"> if the <see cref="SpellChecker"/> is already disposed </exception>
        /// <exception cref="System.IO.IOException"> If there is a low-level I/O error. </exception>
        public void IndexDictionary(IDictionary dict, IndexWriterConfig config, bool fullMerge)
        {
            lock (modifyCurrentIndexLock)
            {
                EnsureOpen();
                Directory dir = this.spellIndex;
                using (var writer = new IndexWriter(dir, config))
                {
                    IndexSearcher indexSearcher = ObtainSearcher();
                    IList<TermsEnum> termsEnums = new List<TermsEnum>();

                    IndexReader reader = searcher.IndexReader;
                    if (reader.MaxDoc > 0)
                    {
                        foreach (AtomicReaderContext ctx in reader.Leaves)
                        {
                            Terms terms = ctx.AtomicReader.Terms(F_WORD);
                            if (terms != null)
                            {
                                termsEnums.Add(terms.GetIterator(null));
                            }
                        }
                    }

                    bool isEmpty = termsEnums.Count == 0;

                    try
                    {
                        IBytesRefIterator iter = dict.EntryIterator;
                        BytesRef currentTerm;

                        while ((currentTerm = iter.Next()) != null)
                        {

                            string word = currentTerm.Utf8ToString();
                            int len = word.Length;
                            if (len < 3)
                            {
                                continue; // too short we bail but "too long" is fine...
                            }

                            if (!isEmpty)
                            {
                                foreach (TermsEnum te in termsEnums)
                                {
                                    if (te.SeekExact(currentTerm))
                                    {
                                        goto termsContinue;
                                    }
                                }
                            }

                            // ok index the word
                            var doc = CreateDocument(word, GetMin(len), GetMax(len));
                            writer.AddDocument(doc);
                        termsContinue:
                            ;
                        }
                    }
                    finally
                    {
                        ReleaseSearcher(indexSearcher);
                    }
                    if (fullMerge)
                    {
                        writer.ForceMerge(1);
                    }
                }
                // TODO: this isn't that great, maybe in the future SpellChecker should take
                // IWC in its ctor / keep its writer open?

                // also re-open the spell index to see our own changes when the next suggestion
                // is fetched:
                SwapSearcher(dir);
            }
        }

        private static int GetMin(int l)
        {
            if (l > 5)
            {
                return 3;
            }
            if (l == 5)
            {
                return 2;
            }
            return 1;
        }

        private static int GetMax(int l)
        {
            if (l > 5)
            {
                return 4;
            }
            if (l == 5)
            {
                return 3;
            }
            return 2;
        }

        private static Document CreateDocument(string text, int ng1, int ng2)
        {
            var doc = new Document();
            // the word field is never queried on... its indexed so it can be quickly
            // checked for rebuild (and stored for retrieval). Doesn't need norms or TF/pos
            Field f = new StringField(F_WORD, text, Field.Store.YES);
            doc.Add(f); // orig term
            AddGram(text, doc, ng1, ng2);
            return doc;
        }

        private static void AddGram(string text, Document doc, int ng1, int ng2)
        {
            int len = text.Length;
            for (int ng = ng1; ng <= ng2; ng++)
            {
                string key = "gram" + ng;
                string end = null;
                for (int i = 0; i < len - ng + 1; i++)
                {
                    string gram = text.Substring(i, ng);
                    FieldType ft = new FieldType(StringField.TYPE_NOT_STORED);
                    ft.IndexOptions = IndexOptions.DOCS_AND_FREQS;
                    Field ngramField = new Field(key, gram, ft);
                    // spellchecker does not use positional queries, but we want freqs
                    // for scoring these multivalued n-gram fields.
                    doc.Add(ngramField);
                    if (i == 0)
                    {
                        // only one term possible in the startXXField, TF/pos and norms aren't needed.
                        Field startField = new StringField("start" + ng, gram, Field.Store.NO);
                        doc.Add(startField);
                    }
                    end = gram;
                }
                if (end != null) // may not be present if len==ng1
                {
                    // only one term possible in the endXXField, TF/pos and norms aren't needed.
                    Field endField = new StringField("end" + ng, end, Field.Store.NO);
                    doc.Add(endField);
                }
            }
        }

        private IndexSearcher ObtainSearcher()
        {
            lock (searcherLock)
            {
                EnsureOpen();
                searcher.IndexReader.IncRef();
                return searcher;
            }
        }

        private void ReleaseSearcher(IndexSearcher aSearcher)
        {
            // don't check if open - always decRef 
            // don't decrement the private searcher - could have been swapped
            aSearcher.IndexReader.DecRef();
        }

        private void EnsureOpen()
        {
            if (disposed)
            {
                throw new AlreadyClosedException("Spellchecker has been closed");
            }
        }

        /// <summary>
        /// Dispose the underlying IndexSearcher used by this SpellChecker </summary>
        /// <exception cref="System.IO.IOException"> if the close operation causes an <see cref="System.IO.IOException"/> </exception>
        /// <exception cref="AlreadyClosedException"> if the <see cref="SpellChecker"/> is already disposed </exception>
        public void Dispose()
        {
            if (!disposed)
            {
                lock (searcherLock)
                {
                    disposed = true;
                    if (searcher != null)
                    {
                        searcher.IndexReader.Dispose();
                    }
                    searcher = null;
                }
            }
        }

        private void SwapSearcher(Directory dir)
        {
            /*
             * opening a searcher is possibly very expensive.
             * We rather close it again if the Spellchecker was closed during
             * this operation than block access to the current searcher while opening.
             */
            IndexSearcher indexSearcher = CreateSearcher(dir);
            lock (searcherLock)
            {
                if (disposed)
                {
                    indexSearcher.IndexReader.Dispose();
                    throw new AlreadyClosedException("Spellchecker has been closed");
                }
                if (searcher != null)
                {
                    searcher.IndexReader.Dispose();
                }
                // set the spellindex in the sync block - ensure consistency.
                searcher = indexSearcher;
                this.spellIndex = dir;
            }
        }

        /// <summary>
        /// Creates a new read-only IndexSearcher </summary>
        /// <param name="dir"> the directory used to open the searcher </param>
        /// <returns> a new read-only IndexSearcher </returns>
        /// <exception cref="System.IO.IOException"> f there is a low-level IO error </exception>
        // for testing purposes
        internal virtual IndexSearcher CreateSearcher(Directory dir)
        {
            return new IndexSearcher(DirectoryReader.Open(dir));
        }

        /// <summary>
        /// Returns <c>true</c> if and only if the <see cref="SpellChecker"/> is
        /// disposed, otherwise <c>false</c>.
        /// </summary>
        /// <returns> <c>true</c> if and only if the <see cref="SpellChecker"/> is
        ///         disposed, otherwise <c>false</c>. </returns>
        internal virtual bool IsDisposed
        {
            get
            {
                return disposed;
            }
        }
    }
}