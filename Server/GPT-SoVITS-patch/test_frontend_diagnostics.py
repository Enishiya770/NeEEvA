"""No model/GPU needed: verify the stream receipt's failure/cancellation contract."""
import ast
import contextlib
import io
import json
import unittest
from apply_frontend_diagnostics import DIAGNOSTICS, LIFECYCLE, patched_api, patched_frontend


class StreamReceiptTests(unittest.TestCase):
    def run_stream(self, iterator, cancel=False):
        namespace={};exec(compile(ast.parse(DIAGNOSTICS),'stream_diagnostics','exec'),namespace)
        stream=namespace['_neeeva_diagnosed_stream'](iterator,'test-request')
        captured=io.StringIO();chunks=[];failure=None
        with contextlib.redirect_stdout(captured):
            try:
                if cancel:chunks.append(next(stream));stream.close()
                else:chunks.extend(stream)
            except RuntimeError as error:failure=error
        record=json.loads(captured.getvalue().split('[TTS/ServerStreamReceipt] ')[1])
        self.assertEqual(record['requestId'],'test-request')
        self.assertEqual(record['bytesYielded'],sum(map(len,chunks)))
        return chunks,record,failure

    def test_success_preserves_every_byte(self):
        chunks,record,error=self.run_stream(iter([b'RIFF',b'pcm']))
        self.assertEqual(chunks,[b'RIFF',b'pcm']);self.assertIsNone(error)
        self.assertEqual(record['outcome'],'completed')

    def test_synthesis_exception_after_header_stays_failed(self):
        def broken():
            yield b'RIFF';yield b'partial-pcm';raise RuntimeError('frontend failed')
        chunks,record,error=self.run_stream(broken())
        self.assertIsInstance(error,RuntimeError)
        self.assertEqual(record['outcome'],'failed')
        self.assertEqual(record['failure']['message'],'frontend failed')
        self.assertEqual(chunks,[b'RIFF',b'partial-pcm'])

    def test_cancel_never_becomes_completed(self):
        _,record,_=self.run_stream(iter([b'RIFF',b'not-yielded']),True)
        self.assertEqual(record['outcome'],'cancelled')

    def test_unknown_source_is_rejected(self):
        for patch in [patched_api,patched_frontend]:
            with self.assertRaises(ValueError):patch('# different installation\n')

    def test_asgi_disconnect_closes_before_gc_and_replacement_completes(self):
        import anyio
        namespace={};exec(DIAGNOSTICS+LIFECYCLE,namespace)
        closed=[];captured=io.StringIO()
        async def run():
            for cancel in [True,False]:
                ready=anyio.Event(); finished=anyio.Event(); sent=[]
                def pipeline():
                    try:
                        yield b'RIFF';yield b'pcm';yield b'end'
                    finally:closed.append(cancel)
                p=pipeline()
                response=namespace['_NeEEvAStreamingResponse'](namespace['_neeeva_diagnosed_stream'](p,str(cancel)),str(cancel),p)
                async def send(message):
                    sent.append(message)
                    if cancel and message.get('body')==b'RIFF':
                        ready.set()
                        await anyio.sleep_forever() # disconnect exactly during send
                    if not message.get('more_body',True):finished.set()
                async def receive():
                    if cancel:await ready.wait()
                    else:await finished.wait();await anyio.sleep_forever()
                    return {'type':'http.disconnect'}
                with anyio.fail_after(3):await response({},receive,send)
                self.assertEqual(closed[-1],cancel)
                if not cancel:self.assertEqual(b''.join(m.get('body',b'') for m in sent),b'RIFFpcmend')
        with contextlib.redirect_stdout(captured):anyio.run(run)
        records=[json.loads(x.split('] ',1)[1]) for x in captured.getvalue().splitlines() if x.startswith('[TTS/ServerStreamReceipt]')]
        self.assertEqual([r['outcome'] for r in records],['cancelled','completed'])
        self.assertIn('disconnect-observed',captured.getvalue())
        self.assertEqual(captured.getvalue().count('generators-closed'),2)


if __name__=='__main__':unittest.main()
