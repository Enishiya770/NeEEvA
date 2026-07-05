using UnityEngine;
using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
public class OpenLocalFile
{

    public string OnGetAudioPath()
    {
        OpenFileName ofn = new OpenFileName();
        ofn.structSize = Marshal.SizeOf(ofn);
        ofn.filter = "音频文件(*.mp3)\0*.mp3;";
        //ofn.filter = _filter;
        ofn.file = new string(new char[256]);
        ofn.maxFile = ofn.file.Length;
        ofn.fileTitle = new string(new char[64]);
        ofn.maxFileTitle = ofn.fileTitle.Length;
        ofn.title = "选择音频";
        //注意 一下项目不一定要全选 但是0x00000008项不要缺少
        ofn.flags = 0x00080000 | 0x00001000 | 0x00000800 | 0x00000200 | 0x00000008;//OFN_EXPLORER|OFN_FILEMUSTEXIST|OFN_PATHMUSTEXIST| OFN_ALLOWMULTISELECT|OFN_NOCHANGEDIR
        if (WindowDll.GetOpenFileName(ofn))
        {
            string _fileName = ofn.fileTitle;
            return ofn.file;
        }
        return "";
    }
    public string OnGetImagePath()
	{
		OpenFileName ofn = new OpenFileName();
		ofn.structSize = Marshal.SizeOf(ofn);
		ofn.filter = "图片(*.jpg*.png)\0*.jpg;*.png";
		//ofn.filter = _filter;
		ofn.file = new string(new char[256]);
		ofn.maxFile = ofn.file.Length;
		ofn.fileTitle = new string(new char[64]);
		ofn.maxFileTitle = ofn.fileTitle.Length;
		ofn.title = "选择图片";
		//注意 一下项目不一定要全选 但是0x00000008项不要缺少
		ofn.flags = 0x00080000 | 0x00001000 | 0x00000800 | 0x00000200 | 0x00000008;//OFN_EXPLORER|OFN_FILEMUSTEXIST|OFN_PATHMUSTEXIST| OFN_ALLOWMULTISELECT|OFN_NOCHANGEDIR
		if (WindowDll.GetOpenFileName(ofn))
		{
			string _fileName = ofn.fileTitle;
			return ofn.file;
		}
		return "";
	}

    public string OnGetVideoPath()
    {
        OpenFileName ofn = new OpenFileName();
        ofn.structSize = Marshal.SizeOf(ofn);
        ofn.filter = "视频文件(*.mp4)\0*.mp4;";
        //ofn.filter = _filter;
        ofn.file = new string(new char[256]);
        ofn.maxFile = ofn.file.Length;
        ofn.fileTitle = new string(new char[64]);
        ofn.maxFileTitle = ofn.fileTitle.Length;
        ofn.title = "选择视频";
        //注意 一下项目不一定要全选 但是0x00000008项不要缺少
        ofn.flags = 0x00080000 | 0x00001000 | 0x00000800 | 0x00000200 | 0x00000008;//OFN_EXPLORER|OFN_FILEMUSTEXIST|OFN_PATHMUSTEXIST| OFN_ALLOWMULTISELECT|OFN_NOCHANGEDIR
        if (WindowDll.GetOpenFileName(ofn))
        {
            string _fileName = ofn.fileTitle;
            return ofn.file;
        }
        return "";
    }
}