using System.Collections.Generic;
using System.Threading.Tasks;
using System.ComponentModel;
using System.Windows.Forms;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Data;
using System.IO;
using System;

using CG.Web.MegaApiClient;

namespace MegaFileSystem
{
    public sealed unsafe partial class InitForm
        : Form
    {
        internal readonly Dictionary<int, char> _chrs;
        internal readonly MegaApiClient _mega;
        internal Settings _settings;


        public InitForm(Settings settings, MegaApiClient mega)
        {
            InitializeComponent();

            _chrs = new Dictionary<int, char>();
            _settings = settings;
            _mega = mega;
        }

        private void InitForm_Load(object sender, EventArgs e)
        {
            Button1_Click(sender, e);

            comboBox1.Focus();
            checkBox1.Checked = _settings.Save;
            checkBox2.Checked = _settings.DeleteCache;
            numericUpDown1.Value = Math.Max(numericUpDown1.Minimum, Math.Min(_settings.CacheSz, numericUpDown1.Maximum));

            if (_settings.Email?.Any() ?? false)
            {
                textBox1.Text = _settings.Email;
                maskedTextBox1.Text = new string('X', 20);
            }

            maskedTextBox1.TextChanged += (s, a) =>
            {
                if (_settings.Hash?.Any() ?? false)
                    _settings.Hash = null;
            };
        }

        private void Button1_Click(object sender, EventArgs e)
        {
            _chrs.Clear();

            comboBox1.Items.Clear();

            foreach (char c in "ABCDEFGHIJKLMNOPQRSTUVWX".Except(from d in DriveInfo.GetDrives()
                                                                 select char.ToUpper(d.Name[0])))
                _chrs[comboBox1.Items.Add($"{c}:/")] = c;

            comboBox1.SelectedIndex = -1;
        }

        private void Button3_Click(object sender, EventArgs e)
        {
            _settings = null;

            Close();
        }

        private void Button2_Click(object sender, EventArgs e)
        {
            void Fail(string s) => MessageBox.Show(this, s, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);

            if (!_chrs.ContainsKey(comboBox1.SelectedIndex))
                Fail("You must select a drive letter for the target drive.");
            else
            {
                MegaApiClient.AuthInfos auth;

                if (_settings.Hash?.Any() ?? false)
                    auth = new MegaApiClient.AuthInfos(_settings.Email, _settings.Hash, _settings.AES ?? new byte[0]);
                else
                    auth = MegaApiClient.GenerateAuthInfos(textBox1.Text, maskedTextBox1.Text);

                try
                {
                    _settings.LastToken = _mega.Login(auth);
                    _settings.Hash = auth.Hash;
                    _settings.Email = auth.Email;
                    _settings.AES = auth.PasswordAesKey;
                    _settings.Save = checkBox1.Checked;
                    _settings.Drive = _chrs[comboBox1.SelectedIndex];
                    _settings.DeleteCache = checkBox2.Checked;
                    _settings.CacheSz = (int)numericUpDown1.Value;

                    Close();
                }
                catch
                {
                    _settings.LastToken = null;

                    Fail("The combination of Email-address and password is invalid.");
                }
            }
        }
    }
}
