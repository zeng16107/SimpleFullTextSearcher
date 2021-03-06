﻿using System;
using System.Threading;
using System.IO;
using System.Threading.Tasks;
using SimpleFullTextSearcher.FileSearcher.EventArgs;
using SimpleFullTextSearcher.FileSearcher.Helpers;

namespace SimpleFullTextSearcher.FileSearcher
{
    public static class Searcher
    {
        #region Asynchronous Events

        public delegate void FoundInfoEventHandler(FoundInfoEventArgs e);
        public static event FoundInfoEventHandler FoundInfo;

        public delegate void SearchInfoEventHandler(SearchInfoEventArgs e);
        public static event SearchInfoEventHandler SearchInfo;

        public delegate void ThreadEndedEventHandler(ThreadEndedEventArgs e);
        public static event ThreadEndedEventHandler ThreadEnded;

        #endregion

        #region Variables

        private static Task _searchTask;
        private static CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private static bool _pauseSearch;
        private static SearcherParams _searchParams;
        private static byte[] _containingBytes;
        private static int _foundedCount;
        private static int _count;

        #endregion

        #region Public Methods

        public static bool Start(SearcherParams searchParams)
        {
            if (_searchTask?.Status == TaskStatus.Running)
                return false;

            // при каждом запуске поиска обнуляем все параметры
            ResetVariables();

            // запоминаем параметры поиска
            _searchParams = searchParams;
            _foundedCount = 0;

            _searchTask = Task.Run(() => SearchTask(), _cancellationTokenSource.Token);

            return true;
        }

        public static void Pause() => _pauseSearch = !_pauseSearch;

        public static void Stop() => _cancellationTokenSource.Cancel();

        #endregion

        #region Private Methods

        private static void ResetVariables()
        {
            _cancellationTokenSource = new CancellationTokenSource();
            _searchTask = null;
            _pauseSearch = false;
            _searchParams = null;
            _containingBytes = null;
            _count = 0;
            _foundedCount = 0;
        }

        private static void SearchTask()
        {
            var success = true;
            var errorMsg = "";

            if ((_searchParams.SearchDir.Length >= 3) && (Directory.Exists(_searchParams.SearchDir)))
            {
                if (_searchParams.ContainingChecked)
                {
                    // конвертируем икомую строку в массив байтов
                    if (_searchParams.ContainingText != "")
                    {
                        try
                        {
                            _containingBytes = _searchParams.Encoding.GetBytes(_searchParams.ContainingText);
                        }
                        catch (Exception)
                        {
                            success = false;
                            errorMsg = $"Строка\r\n{_searchParams.ContainingText}\r\nне может быть конвертирована в массив байтов.";
                        }
                    }
                    else
                    {
                        success = false;
                        errorMsg = "Строка поиска не может быть пустой.";
                    }
                }

                if (success)
                {
                    DirectoryInfo dirInfo = null;
                    try
                    {
                        dirInfo = new DirectoryInfo(_searchParams.SearchDir);
                    }
                    catch (Exception ex)
                    {
                        success = false;
                        errorMsg = ex.Message;
                    }

                    if (success)
                    {
                        SearchDirectory(dirInfo);
                    }
                }
                
            }
            else
            {
                success = false;
                errorMsg = $"Каталог\r\n{_searchParams.SearchDir}\r\nне доступен.";
            }

            _searchTask = null;

            ThreadEnded?.Invoke(new ThreadEndedEventArgs(success, _foundedCount, errorMsg));
        }

        private static void SearchDirectory(DirectoryInfo dirInfo)
        {
            if (!_cancellationTokenSource.IsCancellationRequested)
            {
                while (_pauseSearch)
                {
                    // стоит на паузе
                }

                try
                {
                    foreach (var info in dirInfo.GetFileSystemInfos(_searchParams.FileName))
                    {
                        _count++;
                        SearchInfo?.Invoke(new SearchInfoEventArgs(info, _count));

                        if (_cancellationTokenSource.IsCancellationRequested)
                        {
                            break;
                        }

                        while (_pauseSearch)
                        {
                            // стоит на паузе
                        }

                        if (MatchesRestrictions(info))
                        {
                            _foundedCount++;

                            FoundInfo?.Invoke(new FoundInfoEventArgs(info, _foundedCount));
                        }
                    }
                    
                    if (_searchParams.IncludeSubDirsChecked)
                    {
                        foreach (var subDirInfo in dirInfo.GetDirectories())
                        {
                            if (_cancellationTokenSource.IsCancellationRequested)
                            {
                                break;
                            }

                            while (_pauseSearch)
                            {
                                // стоит на паузе
                            }

                            SearchDirectory(subDirInfo);
                        }
                    }
                }
                catch (Exception)
                {
                    // ignored
                }
            }
        }

        private static bool MatchesRestrictions(FileSystemInfo info)
        {
            var matches = false;

            //ToDo: здесь добавить поиски по различным расширениям файлов (и для незапороленных архивов распаковать сначала, потом все файлы проверять)
            if (_searchParams.ContainingChecked)
            {
                switch (new FileInfo(info.FullName).Extension.ToLower())
                {
                    case ".txt":
                    case ".ini":
                    case ".json":
                    case ".csv":
                        try
                        {
                            var encoding = TextFileEncodingHelper.DetectTextFileEncoding(info.FullName) ??
                                           _searchParams.Encoding;
                            using (var sr = new StreamReader(info.FullName, encoding))
                                matches = sr.ReadToEnd().IndexOf(_searchParams.ContainingText, StringComparison.OrdinalIgnoreCase) >= 0;
                        }
                        catch (Exception)
                        {
                            matches = false;
                        }
                        
                        break;
                    case ".rtf":
                        using (var rtBox = new System.Windows.Forms.RichTextBox())
                        {
                            rtBox.Rtf = File.ReadAllText(info.FullName);
                            matches = rtBox.Text.Replace("\r\n", ",").IndexOf(_searchParams.ContainingText, StringComparison.OrdinalIgnoreCase) >= 0;
                        }
                        break;
                    case ".pdf":
                        matches = FullTextSearchHelper.FindTextInPdf(info.FullName, _searchParams.ContainingText, ref _cancellationTokenSource);
                        break;
                    case ".doc":
                        matches = FullTextSearchHelper.FindTextInDoc(info.FullName, _searchParams.ContainingText);
                        break;
                    case ".docx":
                    case ".html":
                        matches = FullTextSearchHelper.FindTextInDocxHtml(info.FullName, _searchParams.ContainingText, ref _cancellationTokenSource);
                        break;
                    case ".xls":
                    case ".xlsx":
                        matches = FullTextSearchHelper.FindTextInExcell(info.FullName, _searchParams.ContainingText, ref _cancellationTokenSource);
                        break;
                    default:
                        matches = FileContainsBytes(info);
                        break;
                }
            }

            return matches;
        }

        private static bool FileContainsBytes(FileSystemInfo info)
        {
            // определяем кодировку файла
            byte[] oldContainingBytes;
            var oldEncoding = _searchParams.Encoding;
            try
            {
                _searchParams.Encoding = TextFileEncodingHelper.DetectTextFileEncoding(info.FullName) ?? oldEncoding;
                _containingBytes = _searchParams.Encoding.GetBytes(_searchParams.ContainingText);
                oldContainingBytes = _containingBytes;
            }
            catch (Exception)
            {
                oldContainingBytes = _containingBytes;
            }

            var matches = false;
            if (info is FileInfo)
            {
                matches = FileContainsBytes(info.FullName, oldContainingBytes);
            }
            _searchParams.Encoding = oldEncoding;

            return matches;
        }

        /// <summary>
        /// Ищет последовательность байтов в файле
        /// </summary>
        /// <param name="path">Полный путь к файлу</param>
        /// <param name="compare">Искомая последовательность байтов</param>
        /// <returns>Возвращает true, если искомая последовательность байтов была найдена в файле</returns>
        private static bool FileContainsBytes(string path, byte[] compare)
        {
            var contains = false;

            const int blockSize = 4096;
            if ((compare.Length >= 1) && (compare.Length <= blockSize))
            {
                var block = new byte[compare.Length - 1 + blockSize];

                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                    {
                        // читаем первый блок байтов из файла
                        var bytesRead = fs.Read(block, 0, block.Length);

                        do
                        {
                            if (_cancellationTokenSource.IsCancellationRequested)
                                break;

                            // ищем в текущем блоке совпадение послежовательности байтов
                            var endPos = bytesRead - compare.Length + 1;
                            for (var i = 0; i < endPos; i++)
                            {
                                // массив байтов (длиной искомой последовательности байтов) на позиции i из текущего блока сравниваем с искомой последовательностью байтов
                                int j;
                                for (j = 0; j < compare.Length; j++)
                                {
                                    if (block[i + j] != compare[j])
                                    {
                                        break;
                                    }
                                }

                                if (j == compare.Length)
                                {
                                    // блок содержит искомую последовательность байт
                                    contains = true;
                                    break;
                                }
                            }

                            // проверяем, завершен ли поиск
                            if (contains || (fs.Position >= fs.Length))
                            {
                                break;
                            }
                            else
                            {
                                if (_cancellationTokenSource.IsCancellationRequested)
                                    break;

                                // формируем новый блок байтов
                                for (var i = 0; i < (compare.Length - 1); i++)
                                {
                                    block[i] = block[blockSize + i];
                                }

                                // считываем новый массив байтов из файла в блок
                                bytesRead = compare.Length - 1 + fs.Read(block, compare.Length - 1, blockSize);
                            }
                        } while (!_cancellationTokenSource.IsCancellationRequested);
                    }
                }
                catch (Exception)
                {
                    return false;
                }
            }

            return contains;
        }
        #endregion
    }
}