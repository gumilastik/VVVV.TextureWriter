
#region usings
using System;
using System.ComponentModel.Composition;

using VVVV.PluginInterfaces.V1;
using VVVV.PluginInterfaces.V2;
using VVVV.Utils.VColor;
using VVVV.Utils.VMath;

using VVVV.Core.Logging;

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using SlimDX;
using SlimDX.Direct3D9;


using System.Drawing;
using System.Drawing.Imaging;

using System.Runtime.InteropServices;
using System.IO;


using System.Threading.Tasks;
using FFmpeg.AutoGen;



using SlimDX.Direct3D11;
using FeralTic.DX11;
using FeralTic.DX11.Resources;
using VVVV.DX11.Lib.Devices;

#endregion usings

namespace VVVV.DX11.Nodes
{
	#region PluginInfo
	[PluginInfo(Name = "Writer", Category = "DX11.Texture", Version = "AVI", Help = "Record texture to video with FFMPEG", Tags = "", AutoEvaluate = true)]
	#endregion PluginInfo
	public class AVIDX11_TextureWriterNode : IPluginEvaluate, IDX11ResourceDataRetriever, IDisposable
	{
		#region fields & pins
		
		bool isInitialised = false;
		bool isOpen = false;
		
		int width = 0;
		int height = 0;
		
		FFmpegWriter writerFFmpeg;
		
		Task recording;
		
		// dx11 stuff
		public DX11RenderContext AssignedContext
		{
			get;
			set;
		}
		public event DX11RenderRequestDelegate RenderRequest;
		
		DX11StagingTexture2D texture;
		enum STEP { BEGIN, WRITE, END };
		bool isInitTexture = false;
		STEP step = 0;
		DataBox dataBox;
		
		
		[Input("Texture In", Order = 0, IsSingle = true)]
		protected Pin<DX11Resource<DX11Texture2D>> FTextureInput;
		
		[Input("FrameRate", DefaultValue = 30, Order = 1, IsSingle = true)]
		IDiffSpread<int> FFrameRateIn;
		
		[Input("FileName", StringType = StringType.Filename, DefaultString = "filename.avi", Order = 2, IsSingle = true)]
		IDiffSpread<string> FFileNameIn;
		
		[Input("Write", Order = 3, IsSingle = true)]
		IDiffSpread<bool> FWriteIn;
		
		[Output("Recording", Order = 0, IsSingle = true)]
		ISpread<bool> FRecordingOut;
		
		[Output("Error", Order = 1, IsSingle = true)]
		ISpread<string> FErrorOut;
		
		[Import()]
		IPluginHost FHost;
		
		[Import()]
		public ILogger FLogger;
		#endregion fields & pins
		
		unsafe class FFmpegWriter
		{
			static bool isRegister = false;
			
			public string error;
			
			string filename;
			int width;
			int height;
			int framerate;
			
			bool isOpen;
			
			AVFormatContext* formatContext = null;
			AVCodecContext* codecContext = null;
			AVCodec* codec = null;
			AVFrame* frameSrc = null;
			AVFrame* frameDst = null;
			AVPacket* packet = null;
			AVStream* stream = null;
			SwsContext* converterContext = null;
			
			
			long startTicks;
			byte* buffer;
			
			public FFmpegWriter()
			{
				isOpen = false;
			}
			
			public bool open(string filename, int width, int height, int framerate = 25)
			{
				error = "";
				
				if (isOpen)
				{
					close();
				}
				
				this.filename = filename;
				this.width = width;
				this.height = height;
				this.framerate = framerate;
				
				if (!isRegister)
				{
					isRegister = true;
					ffmpeg.av_register_all();
					ffmpeg.avcodec_register_all();
				}
				
				fixed (AVFormatContext** ptr = &formatContext)
				{
					if (ffmpeg.avformat_alloc_output_context2(ptr, ffmpeg.av_guess_format("avi", null, null), null, null) < 0)
					{
						error = "error on alloc context";
						return false;
					}
				}
				
				codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_H264);
				if (codec == null)
				{
					error = "no codec found";
					return false;
				}
				
				codecContext = ffmpeg.avcodec_alloc_context3(codec);
				codecContext->width = width;
				codecContext->height = height;
				codecContext->gop_size = 0; // emit intra frames
				codecContext->pix_fmt = AVPixelFormat.PIX_FMT_YUV420P;
				
				AVDictionary* options = null;
				
				ffmpeg.av_dict_set(&options, "preset", "ultrafast", 0);
				ffmpeg.av_dict_set(&options, "qp", "0", 0);
				
				if (ffmpeg.avcodec_open2(codecContext, codec, &options) < 0)
				{
					error = "could not open codec";
					return false;
				}
				
				stream = ffmpeg.avformat_new_stream(formatContext, codec);
				stream->time_base.den = framerate;
				stream->time_base.num = 1;
				
				ffmpeg.avcodec_free_context(&stream->codec); // fix memory leak
				stream->codec = codecContext;
				
				codecContext->time_base.den = framerate;
				codecContext->time_base.num = 1;
				
				ffmpeg.av_dict_free(&options);
				
				frameSrc = ffmpeg.avcodec_alloc_frame();
				frameDst = ffmpeg.avcodec_alloc_frame();
				
				packet = (AVPacket*)Marshal.AllocHGlobal(Marshal.SizeOf(typeof(AVPacket)));
				ffmpeg.av_init_packet(packet);
				
				if (ffmpeg.avio_open(&formatContext->pb, filename, ffmpeg.AVIO_FLAG_WRITE) < 0)
				{
					error = "could not open file";
					return false;
				}
				
				if (ffmpeg.avformat_write_header(formatContext, null) < 0)
				{
					error = "avformat_write_header not open";
					return false;
				}
				
				converterContext = ffmpeg.sws_getContext(width, height, AVPixelFormat.PIX_FMT_RGBA, width, height, codecContext->pix_fmt, ffmpeg.SWS_BICUBIC, null, null, null);
				
				buffer = (byte*)ffmpeg.av_malloc((uint)ffmpeg.avpicture_get_size(codecContext->pix_fmt, width, height));
				
				ffmpeg.avpicture_fill((AVPicture*)frameDst, (sbyte*)buffer, codecContext->pix_fmt, width, height);
				
				startTicks = DateTime.Now.Ticks;
				
				isOpen = true;
				
				return true;
			}
			
			public bool writeFrame(byte* bytes)
			{
				ffmpeg.avpicture_fill((AVPicture*)frameSrc, (sbyte*)bytes, AVPixelFormat.PIX_FMT_RGBA, width, height);
				
				ffmpeg.sws_scale(converterContext, &frameSrc->data0, frameSrc->linesize, 0, height, &frameDst->data0, frameDst->linesize);
				
				frameDst->pts = ffmpeg.av_rescale_q(codecContext->time_base.den * (DateTime.Now.Ticks - startTicks) / TimeSpan.TicksPerSecond, codecContext->time_base, stream->time_base);
				
				int result = 0;
				ffmpeg.avcodec_encode_video2(codecContext, packet, frameDst, &result);
				
				if (ffmpeg.av_write_frame(formatContext, packet) < 0)
				{
					error = "Error write frame";
				}
				ffmpeg.av_free_packet(packet);
				
				return true;
			}
			
			public bool close()
			{
				if (!isOpen) return false;
				
				ffmpeg.av_write_trailer(formatContext);
				ffmpeg.avio_close(formatContext->pb);
				
				fixed (AVFrame** ptr = &frameSrc)
				{
					ffmpeg.avcodec_free_frame(ptr);
				}
				fixed (AVFrame** ptr = &frameDst)
				{
					ffmpeg.avcodec_free_frame(ptr);
				}
				ffmpeg.sws_freeContext(converterContext);
				ffmpeg.avcodec_close(codecContext);
				ffmpeg.avformat_free_context(formatContext);
				
				//ffmpeg.avformat_close_input(ptr);
				
				ffmpeg.av_free_packet(packet);
				Marshal.FreeHGlobal((IntPtr)packet);
     				
				ffmpeg.av_free(buffer);
				ffmpeg.av_free(codec);
				
				if (isRegister)
				{
					//isRegister = false;
					//ffmpeg.avfilter_uninit();
				}
				
				isOpen = false;
				
				return true;
			}
		}
		
		
		
		//called when data for any output pin is requested
		public void Evaluate(int SpreadMax)
		{
			if (this.FTextureInput.PluginIO.IsConnected)
			{
				if (this.RenderRequest != null) { this.RenderRequest(this, this.FHost); }
				if (this.AssignedContext == null) { return; }
				DX11RenderContext context = this.AssignedContext;
				
				if (!isInitTexture || FTextureInput.IsChanged ) //  TODO: или если изменилась текстура
				{
					//  TODO: проверять кратность 2ум ширины и высоты (%2 == 0)
					width = this.FTextureInput[0][context].Description.Width;//Math.Max((FWidthIn[0] / 2) * 2, 1); // %2 = 0 !
					height = this.FTextureInput[0][context].Description.Height;//Math.Max((FHeightIn[0] / 2) * 2, 1);
					
					// if(Format) // TODO: test format
					
					texture = new DX11StagingTexture2D(context, width, height, SlimDX.DXGI.Format.R8G8B8A8_UNorm);
					isInitTexture = true;
				}
				else
				{
					if (step == STEP.END)
					{
						texture.UnLock();
						step = STEP.BEGIN;
					}
					if (step == STEP.BEGIN)
					{
						//context.CurrentDeviceContext.CopyResource(this.FTextureInput[0][context].Resource, this.texture.Resource);
						texture.CopyFrom(this.FTextureInput[0][context]);
						dataBox = texture.LockForRead();
						step = STEP.WRITE;
					}
				}
				
			}
			else
			{
				DisposeTexture();
			}
			
			
			//recreate render & texture if any input changed
			if (FWriteIn.IsChanged)
			{
				if (FWriteIn[0] == true && (recording == null || recording.IsCompleted)) recording = Task.Factory.StartNew(() => { RecordingFrames(null); });
				else isOpen = false;
			}
			
		}
		
		private void RecordingFrames(Object state)
		{
			long startTicks = DateTime.Now.Ticks;
			long lastframe = DateTime.Now.Ticks;
			
			if (step != STEP.END) step = STEP.BEGIN;
			
			Initialise();
			if (isInitialised == true)
			{
				isOpen = true;
				FRecordingOut[0] = true;
				
				while (isOpen == true)
				{
					if (DateTime.Now.Ticks - lastframe > 10000 * 1000 / FFrameRateIn[0])
					{
						lastframe = DateTime.Now.Ticks;
						
						if (step == STEP.WRITE)
						{
							lastframe = DateTime.Now.Ticks;
							unsafe
							{
								//FLogger.Log(LogType.Debug, "write frame!");
								if (!writerFFmpeg.writeFrame((byte*)dataBox.Data.DataPointer.ToPointer())) FErrorOut[0] = writerFFmpeg.error;
							}
							step = STEP.END;
						}
						
						Thread.Sleep(10);
					}
					
					
				}
				
			}
			
			Dispose();
		}
		
		private void Initialise()
		{
			try
			{
				// create instance of video writer
				writerFFmpeg = new FFmpegWriter();
				// create new video file
				isInitialised = writerFFmpeg.open(FFileNameIn[0], width, height, FFrameRateIn[0]);
				FErrorOut[0] = writerFFmpeg.error;
				
				FLogger.Log(LogType.Debug, "Initialised!");
			}
			catch (Exception e)
			{
				FErrorOut[0] = e.Message;
			}
		}
		
		private void DisposeTexture()
		{
			if (isInitTexture)
			{
				isInitTexture = false;
				texture.Dispose();
			}
		}
		
		public void Dispose()
		{
			isInitialised = false;
			isOpen = false;
			
			writerFFmpeg.close();
			
			
			DisposeTexture();
			
			FRecordingOut[0] = false;
			FLogger.Log(LogType.Debug, "Disposed!");
		}
		
	}
}