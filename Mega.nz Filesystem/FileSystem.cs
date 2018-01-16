using System.Text.RegularExpressions;
using System.Security.AccessControl;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Drawing.Imaging;
using System.Collections;
using System.Linq;
using System.Text;
using System.IO;
using System;

using CG.Web.MegaApiClient;

using DokanNet;
using System.Windows.Forms;

namespace MegaFileSystem
{
    public sealed class FileSystemNode<T>
        : IEnumerable<FileSystemNode<T>>
    {
        public const char PATH_SEPARATOR = '\\';
        public const string PATH_PARENT = "..";
        public const string PATH_SAME = ".";

        public List<FileSystemNode<T>> Children { get; } = new List<FileSystemNode<T>>();
        public FileSystemNode<T> Parent { get; } = null;
        public string Name { get; }
        public T Value { set; get; }


        public bool IsRoot => Parent is null || Parent is default;

        public bool HasChildren => Children.Any();

        public string Path => $"{Parent?.Path ?? ""}{PATH_SEPARATOR}{Name}";

        public FileSystemNode<T> Root => IsRoot ? this : Parent.Root;

        public FileSystemNode<T> this[string path] => Navigate(path);


        public FileSystemNode(string name, FileSystemNode<T> parent)
        {
            Name = name;
            Parent = parent;
        }

        public void ClearChildren(bool recursive = false)
        {
            if (recursive)
                foreach (FileSystemNode<T> c in Children)
                    c?.ClearChildren(recursive);

            Children?.Clear();
        }

        public IEnumerable<FileSystemNode<T>> FindChildren(string regex)
        {
            Regex reg = new Regex(regex, RegexOptions.IgnoreCase);

            return from c in Children
                   where c != null
                   where reg.IsMatch(c.Name)
                   select c;
        }

        public FileSystemNode<T> Navigate(string path)
        {
            path = path.ToLower().Trim();

            if (path == PATH_SEPARATOR.ToString())
                return Root;

            path = path.TrimEnd(PATH_SEPARATOR);

            if (path == PATH_SAME)
                return this;
            else if (path == PATH_PARENT)
                return IsRoot ? this : Parent;
            else if (path.Contains(PATH_SEPARATOR))
            {
                int ndx = path.IndexOf(PATH_SEPARATOR);
                FileSystemNode<T> node = this;

                if (ndx == 0)
                    node = Root;
                else
                {
                    string child = path.Remove(ndx).Trim();

                    if (child == PATH_PARENT)
                        node = IsRoot ? this : Parent;
                    else if (child != PATH_SAME)
                        node = Children.FirstOrDefault(x => x.Name.ToLower() == child);
                }

                return node?.Navigate(path.Substring(ndx + 1));
            }
            else
                return Children.FirstOrDefault(x => x.Name.ToLower() == path);
        }

        public override string ToString() => $"'{Path}' (Children: {Children.Count}, Value: {Value})";

        public IEnumerator<FileSystemNode<T>> GetEnumerator() => Children.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public IEnumerable<FileSystemNode<T>> GetChildren(string path) => Navigate(path) ?? new FileSystemNode<T>[0] as IEnumerable<FileSystemNode<T>>;
    }

    public sealed class FileSystem
        : IDokanOperations
    {
        internal const string CFILE_ICON = "favicon.ico";
        internal const string CFILE_INI = "desktop.ini";
        internal const string LABEL = "MEGA.NZ";

        internal static readonly byte[] ICOBytes;


        internal DirectoryInfo TemporaryDirectory { get; }
        private FileSystemNode<INode> Root { get; set; }
        public MegaApiClient Mega { get; }
        public long CacheSize { get; }
        public string Email { get; }


        public string INIFileContent => $@"
[.ShellClassInfo]
ConfirmFileOp=0
NoSharing=1
IconFile={CFILE_ICON}
IconIndex=0
InfoTip=The MEGA.NZ file volume associated with the account '{Email}'.
".Trim();

        public FileSystemNode<INode> this[string path] => Root[path];


        static FileSystem()
        {
            using (MemoryStream ms = new MemoryStream())
            {
                Properties.Resources.favicon.Save(ms);

                ICOBytes = ms.ToArray();
            }
        }

        public FileSystem(DirectoryInfo tmp, MegaApiClient mega, string email, long cachesz)
        {
            TemporaryDirectory = tmp;
            CacheSize = cachesz;
            Email = email;
            Mega = mega;

            if (!tmp.Exists)
                tmp.Create();

            UpdateNodes();
        }

        public void UpdateNodes(Action<FileSystemNode<INode>> callback = null)
        {
            lock (this)
            {
                INode[] nodes = Mega.GetNodes().ToArray();
                INode rootnd = nodes.First(n => n.Type == NodeType.Root);

                if (Root != null)
                    Root.ClearChildren();

                Root = new FileSystemNode<INode>(rootnd.Name, null)
                {
                    Value = rootnd
                };

                buildtree(Root);
                callback?.Invoke(Root);


                void buildtree(FileSystemNode<INode> nd)
                {
                    foreach (INode child in nodes.Where(x => x.ParentId == nd.Value.Id))
                    {
                        FileSystemNode<INode> childnode = new FileSystemNode<INode>(child.Name, nd)
                        {
                            Value = child
                        };

                        buildtree(childnode);
                        nd.Children.Add(childnode);
                    }
                }
            }
        }

        public (FileSystemNode<INode> parent, string name) GetParentFolder(string file)
        {
            int ndx = file.LastIndexOf('\\');
            string name = file.Substring(ndx + 1);
            FileSystemNode<INode> parent = this[file.Substring(0, ndx)];

            return (parent, name);
        }

        private FileInformation GetFileInfo(FileSystemNode<INode> node)
        {
            if (node?.Value is INode n)
                return new FileInformation
                {
                    Length = n.Size,
                    FileName = n.Name,
                    CreationTime = n.CreationDate,
                    LastWriteTime = n.ModificationDate,
                    LastAccessTime = n.ModificationDate,
                    Attributes = n.Type == NodeType.Directory ? FileAttributes.Directory : FileAttributes.NotContentIndexed,
                };
            else if (node?.Name is string s)
                return new FileInformation { FileName = s };
            else
                return default;
        }

        private bool IsINIFile(string fileName) => fileName.Trim().Replace('\\', '/').ToLower() == $"/{CFILE_INI}";

        private bool IsICOFile(string fileName) => fileName.Trim().Replace('\\', '/').ToLower() == $"/{CFILE_ICON}";

        public void Cleanup(string fileName, DokanFileInfo info)
        {
        }

        public void CloseFile(string fileName, DokanFileInfo info)
        {
        }

        public NtStatus CreateFile(string fileName, DokanNet.FileAccess access, FileShare share, FileMode mode, FileOptions options, FileAttributes attributes, DokanFileInfo info)
        {
            if (attributes.HasFlag(FileAttributes.Directory) || info.IsDirectory)
                try
                {
                    (FileSystemNode<INode> parent, string subfolder) = GetParentFolder(fileName);

                    if ((parent.Value.Type != NodeType.Directory) && (parent.Value.Type != NodeType.Root))
                        return DokanResult.PathNotFound;
                    else if (parent[subfolder] is null)
                        Mega.CreateFolder(subfolder, parent.Value);
                    else
                        return DokanResult.FileExists;
                }
                catch
                {
                    return DokanResult.Unsuccessful;
                }
            else
            {

                // TODO

            }

            UpdateNodes();

            return DokanResult.Success;

            if (IsINIFile(fileName) || IsICOFile(fileName))
                return DokanResult.AlreadyExists;
            else
            {

                return DokanResult.Success;
            }
        }

        public NtStatus DeleteDirectory(string fileName, DokanFileInfo info) => DeleteFile(fileName, info);

        public NtStatus DeleteFile(string fileName, DokanFileInfo info)
        {
            if (IsINIFile(fileName) || IsICOFile(fileName))
                return DokanResult.AccessDenied;
            else
                try
                {
                    FileSystemNode<INode> nd = this[fileName];

                    if (nd?.Value is null)
                        return DokanResult.Unsuccessful;

                    if (nd.IsRoot || (nd.Value.Type == NodeType.Inbox) || (nd.Value.Type == NodeType.Root) || (nd.Value.Type == NodeType.Trash))
                        return DokanResult.AccessDenied;

                    if (nd.Value.Type == NodeType.Trash)
                    {
                        if (MessageBox.Show($@"You are about to delete the following file/directory permanently:
Name: {fileName}
Path: {nd.Path}
Owner: {nd.Value.Owner}
Node type: {nd.Value.Type}
Node ID: {nd.Value.Id}
Parent ID: {nd.Value.ParentId}
Size: {nd.Value.Size / 1024} kB
Date created: {nd.Value.CreationDate}
Date modified: {nd.Value.ParentId}
", "Delete File/Directory permanently", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                            Mega.Delete(nd.Value, false);
                        else
                            return DokanResult.Unsuccessful;
                    }
                    else
                        Mega.Delete(nd.Value, true);

                    UpdateNodes();

                    return DokanResult.Success;
                }
                catch
                {
                    return DokanResult.Unsuccessful;
                }
        }

        public NtStatus FindFiles(string fileName, out IList<FileInformation> files, DokanFileInfo info)
        {
            List<FileSystemNode<INode>> dir = this[fileName]?.Children;

            files = new List<FileInformation>();

            if (dir is null)
                return DokanResult.PathNotFound;
            else
                foreach (FileSystemNode<INode> child in dir)
                    if (child?.Value != null)
                        files.Add(GetFileInfo(child));

            if (fileName.Trim().Replace('\\', '/') == "/")
            {
                files.Add(new FileInformation
                {
                    FileName = CFILE_INI,
                    Length = Encoding.Default.GetBytes(INIFileContent).Length,
                    Attributes = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly
                });
                files.Add(new FileInformation
                {
                    FileName = CFILE_ICON,
                    Length = ICOBytes.Length,
                    Attributes = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly
                });
            }
            
            return DokanResult.Success;
        }

        public NtStatus FindFilesWithPattern(string fileName, string searchPattern, out IList<FileInformation> files, DokanFileInfo info)
        {
            return FindFiles(fileName, out files, info);

            files = new List<FileInformation>();

            foreach (FileSystemNode<INode> child in this[fileName]?.FindChildren(searchPattern.Replace(".", "\\.").Replace("*", ".*"))?.ToArray() ?? new FileSystemNode<INode>[0])
                if (child?.Value is INode n)
                    files.Add(new FileInformation
                    {
                        Length = n.Size,
                        FileName = n.Name,
                        CreationTime = n.CreationDate,
                        LastWriteTime = n.ModificationDate,
                        LastAccessTime = n.ModificationDate,
                        Attributes = (n.Type == NodeType.Directory ? FileAttributes.Directory : FileAttributes.Normal)
                                   | FileAttributes.Encrypted | FileAttributes.NotContentIndexed
                    });

            return DokanResult.Success;
        }

        public NtStatus FindStreams(string fileName, out IList<FileInformation> streams, DokanFileInfo info) => FindFiles(fileName, out streams, info);

        public NtStatus FlushFileBuffers(string fileName, DokanFileInfo info)
        {
            if (IsINIFile(fileName) || IsICOFile(fileName))
                return DokanResult.AccessDenied;
            else
            {


            }

            UpdateNodes();

            return DokanResult.Success;
        }

        public NtStatus GetDiskFreeSpace(out long freeBytesAvailable, out long totalNumberOfBytes, out long totalNumberOfFreeBytes, DokanFileInfo info)
        {
            IAccountInformation acc = Mega.GetAccountInformation();
            
            freeBytesAvailable =
            totalNumberOfFreeBytes = acc.TotalQuota - acc.UsedQuota;
            totalNumberOfBytes = acc.TotalQuota;

            return DokanResult.Success;
        }

        public NtStatus GetFileInformation(string fileName, out FileInformation fileInfo, DokanFileInfo info)
        {
            if (IsINIFile(fileName) || IsICOFile(fileName))
                fileInfo = new FileInformation
                {
                    Length = IsINIFile(fileName) ? Encoding.Default.GetBytes(INIFileContent).Length : ICOBytes.Length,
                    FileName = fileName.Trim('\\', '/'),
                    Attributes = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly
                };
            else
            {
                FileSystemNode<INode> node = this[fileName];

                fileInfo = GetFileInfo(node);

                if (node is null)
                    return DokanResult.FileNotFound;
            }

            return DokanResult.Success;
        }

        public NtStatus GetFileSecurity(string fileName, out FileSystemSecurity security, AccessControlSections sections, DokanFileInfo info)
        {
            security = null;

            return DokanResult.NotImplemented;
        }

        public NtStatus GetVolumeInformation(out string volumeLabel, out FileSystemFeatures features, out string fileSystemName, DokanFileInfo info)
        {
            volumeLabel = LABEL;
            fileSystemName = Email;
            features = FileSystemFeatures.UnicodeOnDisk | FileSystemFeatures.SequentialWriteOnce | FileSystemFeatures.CasePreservedNames;
            
            return DokanResult.Success;
        }

        public NtStatus LockFile(string fileName, long offset, long length, DokanFileInfo info)
        {
            return DokanResult.Success;
        }

        public NtStatus Mounted(DokanFileInfo info) => DokanResult.Success;

        public NtStatus MoveFile(string oldName, string newName, bool replace, DokanFileInfo info)
        {
            if (IsINIFile(oldName) || IsICOFile(oldName))
                return DokanResult.AccessDenied;
            else if (IsINIFile(newName) || IsICOFile(newName))
                return replace ? DokanResult.AccessDenied : DokanResult.FileExists;

            (FileSystemNode<INode> srcpar, string srcname) = GetParentFolder(oldName);
            (FileSystemNode<INode> dstpar, string dstname) = GetParentFolder(newName);

            if ((srcpar?.Value is null) || (dstpar?.Value is null))
                return DokanResult.Unsuccessful;
            else
            {
                // TODO

                //if (srcpar.Value.Id == dstpar.Value.Id)
                //    ;
                //else
                //    Mega.Move(, dstpar);

                UpdateNodes();

                return DokanResult.Success;
            }
        }

        public NtStatus ReadFile(string fileName, byte[] buffer, out int bytesRead, long offset, DokanFileInfo info)
        {
            FileSystemNode<INode> node = this[fileName];
            byte[] bytes;

            bytesRead = 0;

            if (IsINIFile(fileName))
                bytes = Encoding.Default.GetBytes(INIFileContent);
            else if (IsICOFile(fileName))
                bytes = ICOBytes;
            else if (node is null)
                return DokanResult.PathNotFound;
            else if (node.Value?.Type != NodeType.File)
                return DokanResult.FileNotFound;
            else
            {
                Stream read()
                {
                    if (node.Value.Size <= CacheSize)
                    {
                        FileInfo nfo = new FileInfo($"{TemporaryDirectory.FullName}\\{node.Value.Id}");

                        if (!nfo.Exists)
                            Mega.DownloadFile(node.Value, nfo.FullName);

                        return nfo.OpenRead();
                    }
                    else
                        return Mega.Download(node.Value);
                }

                using (MemoryStream ms = new MemoryStream())
                using (Stream s = read())
                {
                    s.CopyTo(ms);
                    ms.Seek(offset, SeekOrigin.Begin);

                    bytesRead = ms.Read(buffer, 0, buffer.Length);
                }

                return DokanResult.Success;
            }

            using (MemoryStream ms = new MemoryStream(bytes))
            {
                ms.Seek(offset, SeekOrigin.Begin);

                bytesRead = ms.Read(buffer, 0, buffer.Length);
            }

            return DokanResult.Success;
        }

        public NtStatus SetAllocationSize(string fileName, long length, DokanFileInfo info)
        {
            if (IsINIFile(fileName) || IsICOFile(fileName))
                return DokanResult.AccessDenied;
            else
            {

            }

            return DokanResult.Success;
        }

        public NtStatus SetEndOfFile(string fileName, long length, DokanFileInfo info)
        {
            if (IsINIFile(fileName) || IsICOFile(fileName))
                return DokanResult.AccessDenied;
            else
            {

            }

            return DokanResult.Success;
        }

        public NtStatus SetFileAttributes(string fileName, FileAttributes attributes, DokanFileInfo info)
        {
            if (IsINIFile(fileName) || IsICOFile(fileName))
                return DokanResult.AccessDenied;
            else
            {

            }

            return DokanResult.Success;
        }

        public NtStatus SetFileSecurity(string fileName, FileSystemSecurity security, AccessControlSections sections, DokanFileInfo info)
        {
            if (IsINIFile(fileName) || IsICOFile(fileName))
                return DokanResult.AccessDenied;
            else
            {

            }

            return DokanResult.Success;
        }

        public NtStatus SetFileTime(string fileName, DateTime? creationTime, DateTime? lastAccessTime, DateTime? lastWriteTime, DokanFileInfo info)
        {
            if (IsINIFile(fileName) || IsICOFile(fileName))
                return DokanResult.AccessDenied;
            else
            {

            }

            return DokanResult.Success;
        }

        public NtStatus UnlockFile(string fileName, long offset, long length, DokanFileInfo info)
        {
            return DokanResult.Success;
        }

        public NtStatus Unmounted(DokanFileInfo info)
        {
            return DokanResult.Success;
        }

        public NtStatus WriteFile(string fileName, byte[] buffer, out int bytesWritten, long offset, DokanFileInfo info)
        {
            bytesWritten = 0;

            if (IsINIFile(fileName) || IsICOFile(fileName))
                return DokanResult.AccessDenied;
            else
            {
                bytesWritten = 0;



                UpdateNodes();

                return DokanResult.Success;
            }
        }
    }
}
