import os
import uvicorn

if __name__ == '__main__':
    uvicorn.run('Server.SingingLearning.app:create_app', factory=True,
                host=os.environ.get('SINGING_LEARNING_HOST', '127.0.0.1'),
                port=int(os.environ.get('SINGING_LEARNING_PORT', '9884')), workers=1)
